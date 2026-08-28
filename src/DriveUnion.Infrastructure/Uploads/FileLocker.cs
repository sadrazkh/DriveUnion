using System.Security.Cryptography;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Reading a file out of Drive, sealing it, putting it back, and only then deleting what it replaced.
///
/// <para><b>The order is the feature.</b> Every other order loses somebody's file to a crash at the
/// wrong moment:</para>
///
/// <list type="number">
///   <item>read the plaintext and write a sealed copy to a new Drive file;</item>
///   <item>ask Drive what it actually stored, and compare the length and the checksum;</item>
///   <item>write the header and repoint the catalogue at the sealed copy;</item>
///   <item><b>then</b> delete the plaintext.</item>
/// </list>
///
/// <para>A process that stops before 3 leaves a sealed copy nobody is pointing at, and the customer
/// still has their file — the next attempt writes a fresh one and the orphan is swept. A process
/// that stops between 3 and 4 leaves a readable copy nobody is pointing at, which is worse, and is
/// why <c>SourceDriveFileId</c> stays on the row until the delete has actually happened: the job is
/// picked up again and finishes the delete rather than leaving a plaintext copy of a file the
/// customer believes is locked.</para>
///
/// <para>This is <c>AccountMigrator</c>'s shape with <c>RemoteFetcher</c>'s sealing loop inside it,
/// and it is deliberately not built on <c>IUploadCoordinator</c>: that path exists to create a
/// <c>StoredFile</c> from a browser's chunks, and this file already has one. Keeping the row means
/// the id, the name, the folder and the tags all survive — the customer's file becomes locked rather
/// than being replaced by a different file that looks like it.</para>
/// </summary>
public sealed class FileLocker(
    DriveUnionDbContext db,
    IDriveClient drive,
    IDriveFolders folders,
    ContentKeyring keyring,
    TimeProvider clock,
    ILogger<FileLocker> logger) : IFileLockRunner
{
    /// <summary>
    /// 8 MiB, a multiple of Drive's 256 KiB rule and of the segment size.
    ///
    /// <para>The same figure <c>RemoteFetcher</c> uses, for the same reason: a sealed segment is a
    /// mebibyte plus sixteen bytes, so ciphertext never lands on a chunk boundary on its own and has
    /// to be buffered until enough of it has accumulated.</para>
    /// </summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    public async Task<int> RunOnceAsync(int most, CancellationToken cancellationToken)
    {
        var claimed = await db.FileLocks
            .Where(l => l.Status == FileLockStatus.Pending || l.Status == FileLockStatus.Running)
            .Take(most)
            .ToListAsync(cancellationToken);

        var done = 0;

        foreach (var job in claimed)
        {
            // A job whose swap already happened and whose delete did not. Nothing is re-sealed; the
            // only thing owed is the delete, and doing it is the whole of finishing.
            if (job.SealedDriveFileId is { Length: > 0 } && !job.SourceRemoved)
            {
                await RemoveSourceAsync(job, cancellationToken);
                done++;
                continue;
            }

            done += await CarryOutAsync(job, cancellationToken) ? 1 : 0;
        }

        return done;
    }

    private async Task<bool> CarryOutAsync(FileLock job, CancellationToken cancellationToken)
    {
        var key = keyring.Get(job.Id);

        // The key lives in this process's memory and nowhere else, so a restart between the request
        // and the work loses it. That is the design rather than a gap — the alternative is a content
        // key on disk, which is the one thing this product promises there is not. It fails with a
        // sentence that tells the customer to ask again, which costs them a passphrase and no bytes.
        if (key is null)
        {
            await FailAsync(job, "the key was not held any more", permanent: true, cancellationToken);
            return true;
        }

        var file = await db.StoredFiles.FirstOrDefaultAsync(
            f => f.Id == job.StoredFileId, cancellationToken);

        if (file is null || file.DeletedAt is not null)
        {
            await FailAsync(job, "the file is no longer there", permanent: true, cancellationToken);
            return true;
        }

        job.Status = FileLockStatus.Running;
        job.Attempts++;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var sealedId = await SealAsync(job, file, key, cancellationToken);

            await SwapAsync(job, file, sealedId, cancellationToken);
            await RemoveSourceAsync(job, cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Locking file {StoredFileId} failed.", job.StoredFileId);

            await FailAsync(
                job,
                "the file could not be locked",
                permanent: job.Attempts >= FileLock.MaxAttempts,
                cancellationToken);

            return job.Status is FileLockStatus.Failed;
        }
    }

    /// <summary>
    /// Steps 1 and 2: the sealed copy, and Drive's own word for what it stored.
    /// </summary>
    private async Task<string> SealAsync(
        FileLock job,
        StoredFile file,
        byte[] key,
        CancellationToken cancellationToken)
    {
        var length = job.PlaintextLength;
        var cipherLength = Du1.CipherLength(length);
        var noncePrefix = Convert.FromBase64String(job.NoncePrefix!);
        var segments = Du1.SegmentCount(length);

        var folderId = await folders.HomeAsync(
            job.GoogleAccountId, job.TenantId, file.OwnerUserId, cancellationToken);

        // The same name and type. Nothing about the file the customer sees is changing — only
        // whether the bytes behind it can be read.
        var session = await drive.BeginResumableUploadAsync(
            job.GoogleAccountId,
            new DriveUploadRequest(file.Name, file.MimeType, cipherLength, folderId),
            cancellationToken);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        var download = await drive.OpenDownloadAsync(
            job.GoogleAccountId, job.SourceDriveFileId!, null, cancellationToken);

        var plain = new byte[Du1.SegmentSize];
        var pending = new byte[ChunkSize + Du1.SegmentSize + Du1.TagBytes];
        var held = 0;
        var read = 0L;
        var sent = 0L;

        DriveFileMetadata? completed = null;

        await using (download)
        {
            for (var index = 0; index < segments; index++)
            {
                var wanted = (int)Math.Min(Du1.SegmentSize, length - read);
                var filled = await ReadExactlyAsync(download.Content, plain, wanted, cancellationToken);

                // The source is the operator's own Drive and it stopped early. Nothing has been
                // deleted and nothing will be: this throws, and step 4 is never reached.
                if (filled < wanted)
                {
                    throw new InvalidOperationException(
                        $"Drive returned {read + filled} of {length} bytes, so there is nothing "
                        + "complete to seal and the readable copy has not been touched.");
                }

                read += filled;

                var sealedSegment = Du1.EncryptSegment(
                    key, noncePrefix, index, index == segments - 1, plain.AsSpan(0, filled));

                sealedSegment.CopyTo(pending.AsSpan(held));
                held += sealedSegment.Length;

                while (held >= ChunkSize)
                {
                    completed = await WriteAsync(
                        job, session.SessionUri, digest, pending, ChunkSize, sent, cipherLength, cancellationToken);

                    sent += ChunkSize;
                    held -= ChunkSize;
                    Buffer.BlockCopy(pending, ChunkSize, pending, 0, held);
                }
            }
        }

        // The last chunk, and the only one allowed to be a partial size.
        if (held > 0)
        {
            completed = await WriteAsync(
                job, session.SessionUri, digest, pending, held, sent, cipherLength, cancellationToken);
        }

        if (completed is null)
        {
            throw new InvalidOperationException(
                "Drive accepted every byte without completing the upload, so there is no file id to "
                + "point the catalogue at and the readable copy has not been touched.");
        }

        await VerifyAsync(
            job, completed.FileId, cipherLength, Convert.ToHexStringLower(digest.GetHashAndReset()), cancellationToken);

        return completed.FileId;
    }

    private async Task<DriveFileMetadata?> WriteAsync(
        FileLock job,
        Uri sessionUri,
        IncrementalHash digest,
        byte[] buffer,
        int count,
        long at,
        long total,
        CancellationToken cancellationToken)
    {
        digest.AppendData(buffer, 0, count);

        using var chunk = new MemoryStream(buffer, 0, count, writable: false);

        var outcome = await drive.WriteChunkAsync(sessionUri, chunk, at, count, total, cancellationToken);

        job.BytesSealed = at + count;
        await db.SaveChangesAsync(cancellationToken);

        return outcome.Completed;
    }

    /// <summary>
    /// Asks Drive what it stored, and throws unless it matches on both counts.
    ///
    /// <para>One request, spent at the one moment worth spending it: everything after this treats
    /// the sealed copy as good enough to delete the readable one. A truncated copy is caught by the
    /// length and a corrupted one only by the checksum, which is why both are compared — and why a
    /// missing checksum is <b>not</b> agreement. "I could not check" must never read as "I checked
    /// and it was fine" on the path that ends in a delete. The same rule <c>AccountMigrator</c>
    /// keeps, for the same reason and with more at stake: there, a wrong answer loses a copy the
    /// customer can still reach; here, it loses the file.</para>
    /// </summary>
    private async Task VerifyAsync(
        FileLock job,
        string sealedFileId,
        long expectedLength,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        var stored = await drive.GetFileAsync(job.GoogleAccountId, sealedFileId, cancellationToken);

        if (stored is null)
        {
            throw new InvalidOperationException(
                "Drive does not report the sealed copy at all, so it cannot be trusted and the "
                + "readable copy has not been touched.");
        }

        if (stored.SizeBytes != expectedLength)
        {
            throw new InvalidOperationException(
                $"The sealed copy is {stored.SizeBytes} bytes and should be {expectedLength}.");
        }

        if (string.IsNullOrEmpty(stored.Md5Checksum))
        {
            throw new InvalidOperationException(
                "Drive reported no checksum for the sealed copy, and «could not check» is not "
                + "«checked and fine» on the path that deletes the readable copy.");
        }

        if (!string.Equals(stored.Md5Checksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The sealed copy's checksum does not match what was sent.");
        }
    }

    /// <summary>
    /// Step 3: the header, the repoint, and every link that promised something else.
    ///
    /// <para>Saved in one <c>SaveChanges</c>, so the catalogue never names a sealed file without a
    /// header to open it by — which would be a file nobody, including its owner, could read.</para>
    /// </summary>
    private async Task SwapAsync(
        FileLock job,
        StoredFile file,
        string sealedFileId,
        CancellationToken cancellationToken)
    {
        db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            Scheme = Du1.Scheme,
            SegmentSize = Du1.SegmentSize,
            NoncePrefix = job.NoncePrefix!,
            PlaintextLength = job.PlaintextLength,
            KdfSalt = job.KdfSalt!,
            KdfIterations = job.KdfIterations,
            WrappedKey = job.WrappedKey!,
        });

        file.DriveFileId = sealedFileId;
        file.SizeBytes = Du1.CipherLength(job.PlaintextLength);

        job.SealedDriveFileId = sealedFileId;

        await db.SaveChangesAsync(cancellationToken);

        // Every link handed out for this file promised bytes somebody could open. They are
        // ciphertext now, and a link that silently turns from «click and it downloads» into «type a
        // passphrase nobody gave you» is worse than a link that has stopped working: the second is
        // a thing the sender can be told about, and the first is a thing the recipient blames
        // themselves for. The owner re-shares with a link key, which is what those are for.
        //
        // Written here rather than through IShareLinkService: that interface is about a customer
        // revoking one link they can see, and this is a consequence of a job — scoped by tenant as
        // everything is, in the same SaveChanges as the swap, so there is no moment where the file
        // is sealed and a link still promises otherwise.
        await db.ShareLinks
            .Where(l => l.TenantId == job.TenantId && l.StoredFileId == file.Id && l.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), cancellationToken);
    }

    /// <summary>
    /// Step 4, and the only step that destroys anything.
    ///
    /// <para>Reached only after the sealed copy is verified and the catalogue points at it. If it
    /// fails, the job stays claimable and is finished on the next pass — the customer's file is
    /// already locked and correct; what is outstanding is a readable copy in the operator's Drive
    /// that nothing points at, which is exactly what <c>SourceDriveFileId</c> is kept for.</para>
    /// </summary>
    private async Task RemoveSourceAsync(FileLock job, CancellationToken cancellationToken)
    {
        try
        {
            await drive.DeleteAsync(job.GoogleAccountId, job.SourceDriveFileId!, cancellationToken);
        }
        catch (DriveApiException exception)
        {
            // Left claimable on purpose. The row still names the readable copy, so the next pass
            // tries again rather than this becoming a plaintext file nobody remembers.
            logger.LogError(
                exception,
                "The sealed copy of file {StoredFileId} is in place but the readable one could not "
                + "be deleted. It will be tried again.",
                job.StoredFileId);

            return;
        }

        job.SourceRemoved = true;
        job.Status = FileLockStatus.Completed;
        job.BytesSealed = Du1.CipherLength(job.PlaintextLength);
        job.FinishedAt = clock.GetUtcNow();

        // The reservation covered the second copy while both existed. One of them is gone now, so
        // what the workspace is holding is the sealed one — settled rather than released, because
        // the ciphertext is a little larger than the plaintext it replaced.
        await TenantStorageMeter.SettleAsync(
            db, job.TenantId, Du1.CipherLength(job.PlaintextLength), 0, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        keyring.Release(job.Id);

        logger.LogInformation("File {StoredFileId} is locked and the readable copy is gone.", job.StoredFileId);
    }

    private async Task FailAsync(
        FileLock job,
        string reason,
        bool permanent,
        CancellationToken cancellationToken)
    {
        job.FailureReason = reason;

        if (permanent)
        {
            job.Status = FileLockStatus.Failed;
            job.FinishedAt = clock.GetUtcNow();

            // Nothing was swapped, so the room taken for the second copy is room nobody is using.
            await TenantStorageMeter.ReleaseAsync(
                db, job.TenantId, Du1.CipherLength(job.PlaintextLength), cancellationToken);

            keyring.Release(job.Id);
        }
        else
        {
            job.Status = FileLockStatus.Pending;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> ReadExactlyAsync(
        Stream source,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < count)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken);
            if (read == 0) break;

            filled += read;
        }

        return filled;
    }
}
