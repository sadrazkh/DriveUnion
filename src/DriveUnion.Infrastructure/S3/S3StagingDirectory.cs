using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.S3;

/// <summary>Where the gateway stages multipart parts, and how little room it will work in.</summary>
public sealed class S3StagingOptions
{
    public const string SectionName = "DriveUnion:S3";

    /// <summary>
    /// The volume parts are staged on. Empty turns multipart off.
    ///
    /// <para>Off rather than defaulted to a temporary directory, deliberately. Staging needs room
    /// equal to the largest object in flight, and choosing an operator's disk for them is choosing
    /// where their server fills up. A gateway that refuses multipart with a clear code is better
    /// than one that works until the volume it picked runs out.</para>
    /// </summary>
    public string? StagingDirectory { get; set; }

    /// <summary>
    /// The free space below which no new part is accepted.
    ///
    /// <para>A watermark and not zero: a volume filled to the last byte takes the database and the
    /// logs with it, and the thing that filled it was one customer's upload.</para>
    /// </summary>
    public long MinFreeBytes { get; set; } = 8L * 1024 * 1024 * 1024;
}

/// <summary>
/// The staging volume: one directory per upload, one file per part.
///
/// <para>Named by upload id and part number rather than by anything the customer chose. A key is
/// theirs to name and can contain anything a file name cannot — a slash, a colon, a traversal — so
/// nothing a caller supplies reaches a path here.</para>
/// </summary>
public sealed class S3StagingDirectory(IOptions<S3StagingOptions> options)
{
    private readonly S3StagingOptions _options = options.Value;

    /// <summary>Whether multipart is available at all on this deployment.</summary>
    public bool IsConfigured => _options.StagingDirectory is { Length: > 0 };

    public bool HasRoomFor(long sizeBytes)
    {
        if (!IsConfigured) return false;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_options.StagingDirectory!));
            if (root is null) return true;

            return new DriveInfo(root).AvailableFreeSpace - sizeBytes >= _options.MinFreeBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A volume that cannot be measured is one this cannot make a promise about. Accepting is
            // the kinder answer — the write below will fail loudly if there is genuinely no room,
            // and refusing every upload because a disk query threw would be worse.
            return true;
        }
    }

    public string DirectoryFor(Guid uploadId) =>
        Path.Combine(_options.StagingDirectory!, uploadId.ToString("N"));

    /// <summary>Part numbers are one-based and padded, so a directory listing sorts the way it reads.</summary>
    public string PathFor(Guid uploadId, int partNumber) =>
        Path.Combine(DirectoryFor(uploadId), $"{partNumber:D5}.part");

    public void EnsureDirectory(Guid uploadId) => Directory.CreateDirectory(DirectoryFor(uploadId));

    /// <summary>Removes an upload's staged bytes. Never throws — a sweep must not stop at one failure.</summary>
    public void Discard(Guid uploadId)
    {
        if (!IsConfigured) return;

        try
        {
            var directory = DirectoryFor(uploadId);

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind rather than escalated. The row is gone either way, so the next sweep will
            // not find it again — which is a real cost, and smaller than a sweeper that dies on a
            // locked file and never reaches the rest.
        }
    }
}
