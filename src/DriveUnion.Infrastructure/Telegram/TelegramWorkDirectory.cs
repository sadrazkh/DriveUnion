using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>Free space on a real volume, which is the one thing about this that cannot be tested.</summary>
public sealed class TelegramDiskSpace : ITelegramDiskSpace
{
    public long? FreeBytesOn(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unmeasurable volume is not a reason to refuse every transfer. The caller treats null
            // as "no answer" and proceeds, which is right on a machine where there is no local
            // server and therefore no working directory at all.
            return null;
        }
    }
}

/// <summary>What one sweep did. Both numbers, because zero deletions has to be provable.</summary>
public sealed record TelegramSweepResult(int FilesDeleted, long BytesDeleted, int FilesRemaining, long BytesRemaining);

/// <summary>
/// The Bot API server writes every file it handles into its working directory and documents no
/// automatic deletion. On a box whose owner says «من جا نداره» that is close to disqualifying, so
/// there are four mechanisms and this class is two of them.
///
/// <list type="number">
/// <item><b>The pre-flight</b> — <see cref="HasRoomFor"/>. No byte-moving operation starts unless the
/// volume can hold the file plus headroom. Beginning a two-gigabyte transfer onto a volume that
/// cannot hold two gigabytes fails at ninety-eight per cent, having read every byte out of storage
/// and filled the disk on the way out.</item>
/// <item><b>The sweep</b> — <see cref="Sweep"/>, for the crash path only. Deletion on success is the
/// normal path and happens in a <c>finally</c>; this is what catches the process that died between
/// the two.</item>
/// </list>
///
/// <para><b>In production a delete count of zero is the good state</b>, which is the opposite of the
/// rule the product's other sweepers follow — theirs delete on the normal path, this one does not. So
/// the health signal is the directory's total size rather than the count, and it is the test suite
/// that insists on a non-zero count: a filesystem sweeper fails silently more easily than a table one,
/// because a wrong path, a permissions error or a directory that is not there all produce exactly zero
/// deletions and no exception.</para>
/// </summary>
public sealed class TelegramWorkDirectory(
    IOptions<TelegramOptions> options,
    ITelegramDiskSpace disk,
    TimeProvider clock,
    ILogger<TelegramWorkDirectory> logger)
{
    private readonly TelegramOptions _options = options.Value;

    /// <summary>The per-bot subdirectory, or null when there is no local server on this machine.</summary>
    public string? PathFor(long? botUserId) => _options.WorkDirectoryFor(botUserId);

    /// <summary>
    /// Whether a transfer of this size may begin.
    ///
    /// <para>True when there is no working directory to measure — development has no local server, so
    /// the bytes never land here and there is nothing to run out of. That is not a hole: the branch
    /// that writes to this volume is the branch that reads a local path, and it does not exist unless
    /// the local server does.</para>
    /// </summary>
    public bool HasRoomFor(long sizeBytes)
    {
        // Measured against the configured root rather than the per-bot subdirectory, because free
        // space is a property of the volume and the two answers are the same one. It also means the
        // pre-flight does not need to know which bot is running, so a chat handler can ask the same
        // question the drainer asks.
        if (_options.WorkDirectory is not { Length: > 0 } path) return true;
        if (disk.FreeBytesOn(path) is not { } free) return true;

        if (free < _options.WorkDirMinFreeBytes)
        {
            // Below the watermark nothing byte-moving is accepted in either direction until the
            // sweeper has brought free space back above it.
            return false;
        }

        return free >= sizeBytes + _options.WorkDirHeadroomBytes;
    }

    /// <summary>
    /// Deletes the local copy, and never throws for the ordinary reasons a file is already gone.
    ///
    /// <para>Called from a <c>finally</c>, on both outcomes. An <c>if (success)</c> would leave
    /// gigabytes behind on exactly the path that fails, which is the path that repeats.</para>
    /// </summary>
    public void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worth a line, because a delete that keeps failing is what fills the volume — and the
            // sweeper below is the backstop that has to pick it up. Never the path: it names the
            // working directory, which nobody outside this process has any use for.
            logger.LogWarning(ex, "A Telegram working-directory file could not be deleted.");
        }
    }

    /// <summary>
    /// One pass. Files older than the configured age go; below the free-space watermark, the oldest
    /// go regardless of age.
    ///
    /// <para>Deleting a five-minute-old file is destructive — it may be an in-flight transfer, which
    /// will then fail — and that is the correct trade. A failed transfer is one error message; a full
    /// volume takes the database and the upload spool down with it.</para>
    /// </summary>
    public TelegramSweepResult Sweep(long? botUserId)
    {
        if (PathFor(botUserId) is not { } path || !Directory.Exists(path))
        {
            return new TelegramSweepResult(0, 0, 0, 0);
        }

        var now = clock.GetUtcNow();
        var cutoff = now - TimeSpan.FromMinutes(_options.WorkDirMaxAgeMinutes);
        var starved = disk.FreeBytesOn(path) is { } free && free < _options.WorkDirMinFreeBytes;

        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The Telegram working directory could not be read.");

            return new TelegramSweepResult(0, 0, 0, 0);
        }

        var deletedFiles = 0;
        var deletedBytes = 0L;
        var remainingFiles = 0;
        var remainingBytes = 0L;

        // Oldest first, so that under the watermark the destructive pass takes the least likely to be
        // in flight before the most likely.
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            var old = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < cutoff;

            if (!old && !starved)
            {
                remainingFiles++;
                remainingBytes += file.Length;
                continue;
            }

            var size = file.Length;

            try
            {
                file.Delete();
                deletedFiles++;
                deletedBytes += size;

                if (starved
                    && disk.FreeBytesOn(path) is { } recovered
                    && recovered >= _options.WorkDirMinFreeBytes)
                {
                    starved = false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the server still holds open. It will be there next minute.
                remainingFiles++;
                remainingBytes += size;

                logger.LogDebug(ex, "A Telegram working-directory file resisted the sweep.");
            }
        }

        return new TelegramSweepResult(deletedFiles, deletedBytes, remainingFiles, remainingBytes);
    }

    /// <summary>
    /// What the operator's screen renders: size, count and the oldest file's age. A non-zero size
    /// sustained across several minutes is the alarm, and it means delete-on-success has stopped.
    /// </summary>
    public (long Bytes, int Files, TimeSpan? OldestAge) Measure(long? botUserId)
    {
        if (PathFor(botUserId) is not { } path || !Directory.Exists(path)) return (0, 0, null);

        try
        {
            var bytes = 0L;
            var count = 0;
            DateTime? oldest = null;

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                count++;
                if (oldest is null || file.LastWriteTimeUtc < oldest) oldest = file.LastWriteTimeUtc;
            }

            return (
                bytes,
                count,
                oldest is { } age ? clock.GetUtcNow() - new DateTimeOffset(age, TimeSpan.Zero) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The Telegram working directory could not be measured.");

            return (0, 0, null);
        }
    }

    /// <summary>
    /// Free space on the volume, for the operator's card and for the startup arithmetic.
    /// </summary>
    public long? FreeBytes(long? botUserId) =>
        PathFor(botUserId) is { } path ? disk.FreeBytesOn(path) : null;
}
