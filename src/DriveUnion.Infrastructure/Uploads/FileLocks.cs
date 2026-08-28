using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Taking the request, and every reason to refuse it.
///
/// <para>Everything expensive happens on the worker. What happens here is the part that must happen
/// while somebody is looking at a screen: deciding whether this can be done at all, and saying so.
/// A lock that is going to fail for want of room should fail now, with a sentence, rather than in
/// four minutes with half an encrypted copy in the operator's Drive.</para>
/// </summary>
public sealed class FileLocks(DriveUnionDbContext db, ContentKeyring keyring, TimeProvider clock)
    : IFileLocks
{
    public async Task<FileLockResult> StartAsync(
        Guid tenantId,
        Guid? requestedByUserId,
        Guid storedFileId,
        EncryptionHeader header,
        byte[] contentKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(contentKey);

        if (!header.IsWellFormed) return Refused(FileLockRefusal.MalformedHeader);

        // Scoped by tenant explicitly, as everything in this product is. A file id from another
        // workspace is not found rather than refused, which is the same answer a file id that never
        // existed gets — the difference between them is a way to ask whether an id is real.
        var file = await db.StoredFiles
            .FirstOrDefaultAsync(
                f => f.Id == storedFileId && f.TenantId == tenantId && f.DeletedAt == null,
                cancellationToken);

        if (file is null) return Refused(FileLockRefusal.UnknownFile);

        var alreadyLocked = await db.FileEncryptions
            .AsNoTracking()
            .AnyAsync(e => e.StoredFileId == storedFileId, cancellationToken);

        // Encrypting ciphertext is a thing that would work and would be wrong: the file would need
        // two passphrases in the right order and the second header would describe the first one's
        // output rather than the customer's file.
        if (alreadyLocked) return Refused(FileLockRefusal.AlreadyLocked);

        var inFlight = await db.FileLocks
            .AsNoTracking()
            .AnyAsync(
                l => l.StoredFileId == storedFileId
                    && (l.Status == FileLockStatus.Pending || l.Status == FileLockStatus.Running),
                cancellationToken);

        if (inFlight) return Refused(FileLockRefusal.AlreadyLocking);

        // The room for the second copy, taken now.
        //
        // Locking is a copy before it is a replacement — the sealed file has to exist and be
        // verified before the readable one can go — so for the length of the job this workspace
        // holds both. Reserving up front is what turns "you do not have room" into a sentence on a
        // screen instead of a failure four minutes in with a half-written copy already stored.
        var wanted = Du1.CipherLength(file.SizeBytes);

        if (!await TenantStorageMeter.TryReserveAsync(db, tenantId, wanted, cancellationToken))
        {
            return Refused(FileLockRefusal.NoRoom);
        }

        var job = new FileLock
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StoredFileId = storedFileId,
            RequestedByUserId = requestedByUserId,
            Status = FileLockStatus.Pending,

            // The catalogue's length and not the browser's. The browser is describing a file it has
            // never read — it knows the passphrase, not the bytes — and a header whose length
            // disagrees with the ciphertext cannot open it.
            PlaintextLength = file.SizeBytes,
            GoogleAccountId = file.GoogleAccountId,
            SourceDriveFileId = file.DriveFileId,
            KdfSalt = header.KdfSalt,
            KdfIterations = header.KdfIterations,
            WrappedKey = header.WrappedKey,
            NoncePrefix = header.NoncePrefix,
            CreatedAt = clock.GetUtcNow(),
        };

        db.FileLocks.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // Held only now, so a refusal above never leaves a key in memory for a job that does not
        // exist. It is released by the runner on every way out — see FileLocker.
        keyring.Hold(job.Id, contentKey);

        return new FileLockResult(job.Id, FileLockRefusal.None);
    }

    public async Task<IReadOnlyList<FileLockView>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await db.FileLocks
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Join(
                db.StoredFiles.AsNoTracking(),
                l => l.StoredFileId,
                f => f.Id,
                (l, f) => new { Lock = l, f.Name })
            .ToListAsync(cancellationToken);

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset, and this has to behave
        // the same on it as on Postgres.
        return
        [
            .. rows
                .OrderByDescending(r => r.Lock.CreatedAt)
                .Select(r => new FileLockView(
                    r.Lock.Id,
                    r.Lock.StoredFileId,
                    r.Name,
                    r.Lock.Status,
                    r.Lock.PlaintextLength,
                    r.Lock.BytesSealed,
                    r.Lock.FailureReason,
                    r.Lock.CreatedAt)),
        ];
    }

    private static FileLockResult Refused(FileLockRefusal refusal) => new(null, refusal);
}
