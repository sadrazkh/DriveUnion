using System.Security.Cryptography;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// Moves files off one Google account and onto another, and takes the old copies away afterwards.
///
/// <para><b>Through this server, because there is no other way.</b> Drive can copy a file within one
/// account in a single API call, and between two accounts only if the file is shared from one to the
/// other — which would mean granting a second Google identity access to a customer's bytes, exactly
/// the thing this product exists not to do. So the file is read out of the source and written into
/// the target, one buffer at a time, and nothing is ever held: this is the same discipline the public
/// download path follows, for the same reason.</para>
///
/// <para><b>What makes it safe to delete afterwards.</b> The bytes are hashed on the way through, and
/// what Drive reports for the finished copy is compared against that hash and that length. Only then
/// does the catalogue stop pointing at the source — and even then the source is left standing, for
/// the sweeper to take once nothing can still be reading it.</para>
///
/// <para><b>Encrypted files cost nothing extra.</b> What moves is ciphertext, the key is not needed
/// and is not present, and the <c>FileEncryption</c> row is untouched because it names no account and
/// no Drive id. That is not a coincidence — the header was deliberately built out of what describes
/// the ciphertext rather than where it lives.</para>
/// </summary>
public sealed class AccountMigrator(
    DriveUnionDbContext db,
    IDriveClient drive,
    IDriveFolders folders,
    TimeProvider clock,
    ILogger<AccountMigrator> logger) : IAccountMigrator
{
    /// <summary>
    /// What is read from the source and written to the target in one go.
    ///
    /// <para>8 MiB rather than the 32 MiB an upload from a browser uses: this runs unattended and in
    /// parallel with everything else the server is doing, and a resumable chunk is held in the
    /// request to Google for as long as it takes to send. Smaller chunks mean a stall costs less and
    /// the memory this worker occupies stays bounded whatever else is happening.</para>
    /// </summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    private const int CopyBuffer = 81920;

    public async Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken)
    {
        if (budget <= 0) return 0;

        var migration = await NextDueAsync(cancellationToken);
        if (migration is null) return 0;

        if (migration.Status == AccountMigrationStatus.Pending)
        {
            migration.Status = AccountMigrationStatus.Running;
            await db.SaveChangesAsync(cancellationToken);
        }

        var moved = 0;

        for (var i = 0; i < budget && !cancellationToken.IsCancellationRequested; i++)
        {
            var file = await NextFileAsync(migration, cancellationToken);

            if (file is null)
            {
                // A pass found nothing. Not «no files exist» — «nothing this migration can still
                // act on», which is what finished means when the file list is a query rather than a
                // snapshot.
                await FinishAsync(migration, cancellationToken);
                break;
            }

            if (await MoveOneAsync(migration, file, cancellationToken)) moved++;
        }

        return moved;
    }

    public async Task<int> SweepMovedSourcesAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Ordered and filtered in memory: SQLite will not compare a DateTimeOffset in SQL, and this
        // code has to behave the same on it as on Postgres. The same reason ShareLinkService sorts
        // its links here rather than in the query.
        var due = (await db.FileRelocations
                .Where(r => r.Status == FileRelocationStatus.Moved)
                .ToListAsync(cancellationToken))
            .Where(r => r.MovedAt is { } at && now - at >= FileRelocation.Grace)
            .ToList();

        var swept = 0;

        foreach (var relocation in due)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                // Nothing here can lose a file. Every row reaching this point has a verified target
                // copy that the catalogue already points at; what is being deleted is the duplicate.
                await drive.DeleteAsync(
                    relocation.SourceAccountId,
                    relocation.SourceDriveFileId,
                    cancellationToken);

                relocation.Status = FileRelocationStatus.SourceRemoved;
                swept++;
            }
            catch (Exception exception) when (exception is DriveApiException or DriveRateLimitedException)
            {
                // Left as Moved so the next sweep tries again. The account keeps the bytes until it
                // succeeds, which is the right way round: a row marked done for a copy that is still
                // there is space nothing will ever reclaim.
                logger.LogWarning(
                    exception,
                    "Could not delete the source copy of file {StoredFileId} after its move.",
                    relocation.StoredFileId);
            }
        }

        if (swept > 0) await db.SaveChangesAsync(cancellationToken);

        return swept;
    }

    /// <summary>The oldest migration that still has work, running ones before pending ones.</summary>
    private async Task<AccountMigration?> NextDueAsync(CancellationToken cancellationToken)
    {
        var live = await db.AccountMigrations
            .Where(m => m.Status == AccountMigrationStatus.Running
                || m.Status == AccountMigrationStatus.Pending)
            .ToListAsync(cancellationToken);

        // In memory for the DateTimeOffset reason above. One operator's pool has a handful of these
        // rows in its lifetime, not a page of them.
        return live
            .OrderBy(m => m.Status == AccountMigrationStatus.Running ? 0 : 1)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The next file still on the source that this migration has not given up on.
    ///
    /// <para>Asked again on every file rather than taken from a list, so an upload that lands on the
    /// source mid-drain is picked up and a file somebody deleted is not chased. Live files only:
    /// what is in the trash is on its way out and the purge is what removes it.</para>
    /// </summary>
    private async Task<StoredFile?> NextFileAsync(
        AccountMigration migration,
        CancellationToken cancellationToken)
    {
        var abandoned = await db.FileRelocations
            .Where(r => r.MigrationId == migration.Id && r.Status == FileRelocationStatus.Failed)
            .Select(r => r.StoredFileId)
            .ToListAsync(cancellationToken);

        return await db.StoredFiles
            .Where(f => f.GoogleAccountId == migration.SourceAccountId
                && f.DeletedAt == null
                && !abandoned.Contains(f.Id))
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> MoveOneAsync(
        AccountMigration migration,
        StoredFile file,
        CancellationToken cancellationToken)
    {
        var relocation = await db.FileRelocations
            .FirstOrDefaultAsync(
                r => r.MigrationId == migration.Id && r.StoredFileId == file.Id,
                cancellationToken);

        if (relocation is null)
        {
            relocation = new FileRelocation
            {
                Id = Guid.NewGuid(),
                MigrationId = migration.Id,
                StoredFileId = file.Id,
                SourceAccountId = migration.SourceAccountId,
                SourceDriveFileId = file.DriveFileId,
                TargetAccountId = migration.TargetAccountId,
                Status = FileRelocationStatus.Moved,
                CreatedAt = clock.GetUtcNow(),
            };

            db.FileRelocations.Add(relocation);
        }

        relocation.Attempts++;

        try
        {
            var landed = await CopyAsync(migration, file, cancellationToken);

            relocation.TargetDriveFileId = landed.DriveFileId;
            relocation.MovedAt = clock.GetUtcNow();
            relocation.Status = FileRelocationStatus.Moved;
            relocation.FailureReason = null;

            // The swap. From here the product serves the target copy and the source is a duplicate
            // waiting for the sweeper — which is why this and the relocation row are one
            // SaveChanges: a catalogue pointing at the target with no row naming the source is bytes
            // nothing will ever reclaim.
            file.GoogleAccountId = migration.TargetAccountId;
            file.DriveFileId = landed.DriveFileId;
            file.DriveFolderId = landed.FolderId;

            // The restore folder belonged to the old account and means nothing on the new one. Null
            // rather than stale: FileCatalog treats a missing one as «put it back in the home
            // folder», which is right, and a stale id would send a restore into a folder on an
            // account the file is no longer on.
            file.RestoreFolderId = null;

            migration.FilesMoved++;
            migration.BytesMoved += file.SizeBytes;

            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is DriveApiException
            or DriveRateLimitedException
            or IOException
            or InvalidOperationException)
        {
            relocation.FailureReason = Trimmed(exception.Message);

            if (relocation.Attempts >= FileRelocation.MaxAttempts)
            {
                // Given up on, and counted. One file Drive will not hand over must not strand the
                // thirty thousand behind it — and the row says which one, so somebody can look.
                relocation.Status = FileRelocationStatus.Failed;
                migration.FilesFailed++;

                logger.LogError(
                    exception,
                    "Gave up moving file {StoredFileId} after {Attempts} attempts.",
                    file.Id,
                    relocation.Attempts);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "Attempt {Attempt} to move file {StoredFileId} failed.",
                    relocation.Attempts,
                    file.Id);
            }

            await db.SaveChangesAsync(cancellationToken);

            return false;
        }
    }

    /// <summary>
    /// Reads the file out of the source, writes it into the target, and refuses to say it worked
    /// until Drive agrees about both the length and the bytes.
    /// </summary>
    private async Task<(string DriveFileId, string FolderId)> CopyAsync(
        AccountMigration migration,
        StoredFile file,
        CancellationToken cancellationToken)
    {
        var folderId = await folders.HomeAsync(
            migration.TargetAccountId, file.TenantId, file.OwnerUserId, cancellationToken);

        var session = await drive.BeginResumableUploadAsync(
            migration.TargetAccountId,
            new DriveUploadRequest(file.Name, file.MimeType, file.SizeBytes, folderId),
            cancellationToken);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        var download = await drive.OpenDownloadAsync(
            migration.SourceAccountId, file.DriveFileId, null, cancellationToken);

        DriveFileMetadata? completed = null;
        var sent = 0L;

        await using (download)
        {
            var buffer = new byte[ChunkSize];

            while (sent < file.SizeBytes)
            {
                var wanted = (int)Math.Min(ChunkSize, file.SizeBytes - sent);
                var filled = await ReadExactlyAsync(download.Content, buffer, wanted, cancellationToken);

                if (filled == 0)
                {
                    throw new InvalidOperationException(
                        $"The source stopped after {sent} of {file.SizeBytes} bytes, so there is "
                        + "nothing complete to write and the source has not been touched.");
                }

                digest.AppendData(buffer, 0, filled);

                using var chunk = new MemoryStream(buffer, 0, filled, writable: false);

                var outcome = await drive.WriteChunkAsync(
                    session.SessionUri, chunk, sent, filled, file.SizeBytes, cancellationToken);

                sent += filled;

                if (outcome.Completed is { } metadata) completed = metadata;
            }
        }

        if (completed is null)
        {
            throw new InvalidOperationException(
                "The target accepted every byte without ever completing the upload, so there is no "
                + "file id to point the catalogue at.");
        }

        var expected = Convert.ToHexStringLower(digest.GetHashAndReset());

        await VerifyAsync(migration.TargetAccountId, completed.FileId, file, expected, cancellationToken);

        return (completed.FileId, folderId);
    }

    /// <summary>
    /// Asks Drive what it actually stored, and throws unless it matches.
    ///
    /// <para>One request, spent at the one moment it is worth spending: everything after this treats
    /// the target as good enough to stop pointing at the source. A truncated copy is caught by the
    /// length; a corrupted one only by the checksum, which is why both are compared.</para>
    ///
    /// <para>A target that reports no checksum is <b>not</b> treated as agreement. It is a real
    /// possibility — Drive omits one for its own document types — and «I could not check» must never
    /// read as «I checked and it was fine» on the path that ends in a delete.</para>
    /// </summary>
    private async Task VerifyAsync(
        Guid targetAccountId,
        string targetDriveFileId,
        StoredFile file,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        var landed = await drive.GetFileAsync(targetAccountId, targetDriveFileId, cancellationToken);

        if (landed is null)
        {
            throw new InvalidOperationException(
                $"The target reported file {targetDriveFileId} complete and then could not find it.");
        }

        if (landed.SizeBytes != file.SizeBytes)
        {
            throw new InvalidOperationException(
                $"The copy of {file.Id} is {landed.SizeBytes} bytes and the original is "
                + $"{file.SizeBytes}, so it was not copied whole.");
        }

        if (landed.Md5Checksum is not { Length: > 0 } actual)
        {
            throw new InvalidOperationException(
                $"The target gave no checksum for the copy of {file.Id}, so there is nothing to "
                + "check it against and the source will not be given up.");
        }

        if (!string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The copy of {file.Id} is the right length and the wrong bytes.");
        }
    }

    /// <summary>
    /// Fills as much of <paramref name="wanted"/> as the source will give, and returns how much.
    ///
    /// <para>A resumable chunk has to be exactly the length its Content-Range claims, and a single
    /// read off a network stream returns whatever happened to arrive. Short of the whole file this
    /// loop is the difference between a chunk Drive accepts and one it rejects for a reason that
    /// reads like a protocol bug.</para>
    /// </summary>
    private static async Task<int> ReadExactlyAsync(
        Stream source,
        byte[] buffer,
        int wanted,
        CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < wanted)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(filled, Math.Min(CopyBuffer, wanted - filled)),
                cancellationToken);

            if (read == 0) break;

            filled += read;
        }

        return filled;
    }

    private async Task FinishAsync(AccountMigration migration, CancellationToken cancellationToken)
    {
        migration.Status = AccountMigrationStatus.Completed;
        migration.FinishedAt = clock.GetUtcNow();

        logger.LogInformation(
            "Finished draining account {SourceAccountId}: {Moved} files moved, {Failed} left behind.",
            migration.SourceAccountId,
            migration.FilesMoved,
            migration.FilesFailed);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Trimmed(string message) =>
        message.Length <= AccountMigration.MaxFailureReasonLength
            ? message
            : message[..AccountMigration.MaxFailureReasonLength];
}
