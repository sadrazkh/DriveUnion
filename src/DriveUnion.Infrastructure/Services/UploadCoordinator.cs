using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Plans;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// Browser → OVH → Google, one chunk at a time.
///
/// The request body is handed to <see cref="IDriveClient"/> as the very stream this class received.
/// Nothing here reads it, measures it, or copies it: a 96 GB upload spooled to memory or disk is a
/// 96 GB bug, and it is the kind that only shows up on the one file big enough to matter.
///
/// <para><b>This is where a plan refuses an upload, and it is deliberately not a controller.</b>
/// There are already three callers of this path — the panel's uploader island, <c>/api/uploads</c>
/// and the Telegram inbound bridge — and a transfer queue and an export will add more. A check in one
/// controller is a check the other entry points do not have, and the missing one will be the one that
/// matters. Since the bot moved to a self-hosted Bot API server its inbound ceiling is 2000 MB rather
/// than 20 MB, which turned the bridge from the caller that could never trip a per-file limit into the
/// caller that trips it most often.</para>
/// </summary>
public sealed class UploadCoordinator(
    DriveUnionDbContext db,
    IDriveClient drive,
    IDriveFolders folders,
    IUploadTargetSelector targetSelector,
    TimeProvider clock) : IUploadCoordinator
{
    /// <summary>A browser that sends no type still gets a file, not a rejection.</summary>
    private const string FallbackMimeType = "application/octet-stream";

    /// <summary>
    /// The four-argument shape several harnesses outside this slice already build by hand.
    ///
    /// <para>It resolves folders through a cache of its own rather than the process-wide one, which
    /// is a difference in requests spent and not in behaviour: find-or-create is idempotent, and the
    /// cache only removes the asking. Anything resolved from the container gets the registered
    /// resolver and the shared cache.</para>
    /// </summary>
    public UploadCoordinator(
        DriveUnionDbContext db,
        IDriveClient drive,
        IUploadTargetSelector targetSelector,
        TimeProvider clock)
        : this(db, drive, new DriveFolders(db, drive, new DriveFolderCache()), targetSelector, clock)
    {
    }

    /// <summary>
    /// The same begin, told who is uploading, so the bytes land in that person's folder.
    ///
    /// <para>The owner is an argument and not something this class goes and finds. An ambient
    /// principal read out of the request would make the folder a file lands in depend on state
    /// nothing in the signature mentions, and this product scopes by explicit argument everywhere —
    /// the model's own note about having no global query filters is the same decision.</para>
    ///
    /// <para>It is an overload because <see cref="IUploadCoordinator"/> lives in Core and takes a
    /// tenant only. The panel is the one caller that knows who is signed in, and it reaches this
    /// class through that interface, so until the interface carries a user the panel's uploads land
    /// in the tenant folder.</para>
    /// </summary>
    public async Task<BeginUploadResult> BeginAsync(
        Guid tenantId,
        Guid? ownerUserId,
        BeginUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("An upload needs a file name.", nameof(request));
        }

        if (request.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "An upload cannot be negative.");
        }

        // The slug used to be read here to build the folder path. It is the resolver's to read now,
        // and what is left is the one number this method enforces.
        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.MaxFileBytes })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} does not exist.");

        // The per-file limit, first, and before anything has been spent.
        //
        // It takes no reservation, and that is the rule working rather than an exception to it: it is
        // a predicate on one immutable declared value, not a claim on a shared counter. Nothing
        // another request does can make a 3 GB file bigger, so there is no slot to race for.
        //
        // Evaluated before the storage reserve so that a refused upload spends nothing at all — the
        // alternative is reserving and then unwinding, and an unwind that fails leaves the customer
        // paying for a file that was never accepted.
        if (request.SizeBytes > tenant.MaxFileBytes)
        {
            throw PlanLimitExceededException.File(request.SizeBytes, tenant.MaxFileBytes);
        }

        // Then the tenant's room, taken before Google is contacted.
        //
        // The plan check runs ahead of the pool's: a tenant who is both over their cap and facing an
        // empty pool is told about their cap, because that is true either way and they can act on it,
        // while "storage is busy, retry at 10:30" promises a retry that will not help them.
        if (!await TenantStorageMeter.TryReserveAsync(db, tenantId, request.SizeBytes, cancellationToken))
        {
            var (used, quota) = await TenantStorageMeter.ReadAsync(db, tenantId, cancellationToken);

            throw PlanLimitExceededException.Storage(request.SizeBytes, used, quota);
        }

        try
        {
            var accountId = await targetSelector.SelectAsync(request.SizeBytes, cancellationToken)
                ?? throw new UploadRejectedException(
                    $"No connected Google account can accept a file of {request.SizeBytes} bytes.");

            // The account is confirmed to still be there and nothing about it is loaded: where a
            // file goes is the resolver's answer now, and the row's remembered root is its business.
            var accountExists = await db.GoogleAccounts
                .AsNoTracking()
                .AnyAsync(a => a.Id == accountId, cancellationToken);

            if (!accountExists)
            {
                throw new UploadRejectedException(
                    $"Google account {accountId} was selected for this upload but no longer exists.");
            }

            // Asked for rather than derived here, and asked for once: the delete path needs the same
            // answer later, and two derivations of one layout is how two folders with the same name
            // end up holding half of somebody's files each.
            var folderId = await folders.HomeAsync(accountId, tenantId, ownerUserId, cancellationToken);

            var mimeType = string.IsNullOrWhiteSpace(request.MimeType) ? FallbackMimeType : request.MimeType;

            var driveSession = await drive.BeginResumableUploadAsync(
                accountId,
                new DriveUploadRequest(request.FileName, mimeType, request.SizeBytes, folderId),
                cancellationToken);

            var session = new UploadSession
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                GoogleAccountId = accountId,

                // Both are carried on the session because the row that will need them is created by
                // a different request — whichever one lands the last chunk, which for a large file is
                // hours later. Re-deriving the folder there would mean resolving a layout that may
                // have been edited across the week a resumable session can live.
                OwnerUserId = ownerUserId,
                DriveFolderId = folderId,

                FileName = request.FileName,
                MimeType = mimeType,
                SizeBytes = request.SizeBytes,
                DriveResumableUri = driveSession.SessionUri.ToString(),
                BytesConfirmed = 0,
                Status = UploadSessionStatus.InProgress,
                CreatedAt = clock.GetUtcNow(),
                ExpiresAt = driveSession.ExpiresAt,
            };

            db.UploadSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);

            return new BeginUploadResult(session.Id, UploadChunking.DefaultChunkSize);
        }
        catch
        {
            // Reserve-then-commit, with the compensating half spelled out rather than implied. The
            // reservation is durable across the Google round trip on purpose — that is what stops two
            // concurrent uploads spending the same free bytes — so the only thing that can give it
            // back is this.
            //
            // CancellationToken.None: a cancelled request is the commonest reason to be here, and a
            // release that inherits the cancellation is a reservation that is never returned.
            await TenantStorageMeter.ReleaseAsync(db, tenantId, request.SizeBytes, CancellationToken.None);

            throw;
        }
    }

    public async Task<UploadProgress> WriteChunkAsync(
        Guid tenantId,
        Guid sessionId,
        Stream content,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(tenantId, sessionId, cancellationToken);

        // A session that is already finished, failed or abandoned reports itself rather than
        // throwing. The client reads Status and FailureReason and knows what to do next; the same
        // answer also makes a replayed final chunk harmless instead of a second StoredFile row.
        if (session.Status != UploadSessionStatus.InProgress)
        {
            return await DescribeAsync(session, cancellationToken);
        }

        if (await FailIfExpiredAsync(session, cancellationToken)) return Describe(session, null);

        if (!UploadChunking.IsValidChunk(offset, length, session.SizeBytes))
        {
            // Drive does not reject a badly sized chunk loudly — it stops acknowledging bytes, which
            // reads like a stalled network. Saying so here keeps it a 400 instead of a support call.
            throw new ArgumentException(
                $"Chunk {offset}+{length} of {session.SizeBytes} is not acceptable: every chunk but "
                + $"the last must be a multiple of {UploadChunking.DriveChunkMultiple} bytes and no "
                + "chunk may run past the end of the file.",
                nameof(length));
        }

        DriveChunkOutcome outcome;
        try
        {
            // `content` is passed through untouched. Do not wrap, copy or measure it here.
            outcome = await drive.WriteChunkAsync(
                new Uri(session.DriveResumableUri),
                content,
                offset,
                length,
                session.SizeBytes,
                cancellationToken);
        }
        catch (DriveUploadSessionExpiredException ex)
        {
            await FailAsync(session, ex.Message, cancellationToken);
            return Describe(session, null);
        }

        // The byte counter, and the second half of the per-file limit.
        //
        // A declared size is a claim, and a pre-check against a claim is not a control: declare one
        // megabyte, push a hundred gigabytes. What the reservation was taken against is
        // session.SizeBytes, and what Drive has acknowledged is a measurement of the body that
        // actually crossed this box — so the moment the acknowledged total passes the reservation,
        // the session is over. Nothing here counts the request stream itself: it is forwarded
        // untouched, and a counter wrapped round a 96 GB body is the bug this whole path avoids.
        if (outcome.ConfirmedLength > session.SizeBytes)
        {
            await FailAsync(
                session,
                $"This upload declared {session.SizeBytes} bytes and has already sent "
                + $"{outcome.ConfirmedLength}. Start it again with the real size.",
                cancellationToken);

            throw PlanLimitExceededException.File(outcome.ConfirmedLength, session.SizeBytes);
        }

        session.BytesConfirmed = outcome.ConfirmedLength;

        Guid? storedFileId = null;
        long? storedSizeBytes = null;
        if (outcome.Completed is { } metadata)
        {
            // Both come off the session rather than being resolved again. This request is not the one
            // that chose the folder — it is whichever one landed the last chunk, hours later for a
            // large file — so re-deriving here would read a layout that may have changed in between,
            // and would put the row's idea of where the file is at odds with where it actually went.
            //
            // A session opened before these columns existed carries null, and a row with a null
            // DriveFolderId is exactly what the delete path already handles: ask Drive which folder
            // this is in, once.
            var stored = new StoredFile
            {
                Id = Guid.NewGuid(),
                TenantId = session.TenantId,
                GoogleAccountId = session.GoogleAccountId,
                OwnerUserId = session.OwnerUserId,
                DriveFolderId = session.DriveFolderId,
                DriveFileId = metadata.FileId,
                Name = metadata.Name,
                MimeType = metadata.MimeType,
                SizeBytes = metadata.SizeBytes,
                CreatedAt = metadata.CreatedTime,
                ModifiedAt = metadata.ModifiedTime,
            };

            db.StoredFiles.Add(stored);
            session.Status = UploadSessionStatus.Completed;
            session.StoredFileId = stored.Id;
            storedFileId = stored.Id;
            storedSizeBytes = metadata.SizeBytes;
        }

        if (storedSizeBytes is not { } actualSize)
        {
            // A chunk that did not finish the file moves one number on one row and settles nothing:
            // the reservation still stands, because the upload still might.
            await db.SaveChangesAsync(cancellationToken);

            return Describe(session, storedFileId);
        }

        // The settle. The file row, the session's new state and the tenant's counter are one unit:
        // a completed upload whose reservation was never replaced leaves the customer paying the
        // declared size for ever, and a settled counter with no file row bills them for nothing.
        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        await TenantStorageMeter.SettleAsync(
            db, session.TenantId, session.SizeBytes, actualSize, cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return Describe(session, storedFileId);
    }

    public async Task<UploadProgress> GetProgressAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(tenantId, sessionId, cancellationToken);

        if (session.Status != UploadSessionStatus.InProgress)
        {
            return await DescribeAsync(session, cancellationToken);
        }

        if (await FailIfExpiredAsync(session, cancellationToken)) return Describe(session, null);

        try
        {
            // Google is asked what it holds. Our BytesConfirmed is a record of what we sent, and a
            // chunk that died on the wire was sent just as thoroughly as one that arrived.
            var confirmed = await drive.GetConfirmedLengthAsync(
                new Uri(session.DriveResumableUri), session.SizeBytes, cancellationToken);

            if (confirmed != session.BytesConfirmed)
            {
                session.BytesConfirmed = confirmed;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DriveUploadSessionExpiredException ex)
        {
            await FailAsync(session, ex.Message, cancellationToken);
        }

        return Describe(session, null);
    }

    private async Task<UploadSession> LoadSessionAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        // The tenant is part of the lookup, not a check after it. A session belonging to somebody
        // else and a session that never existed produce the same message on purpose: a
        // distinguishable "not yours" turns session ids into something worth guessing.
        return await db.UploadSessions
                   .FirstOrDefaultAsync(
                       u => u.Id == sessionId && u.TenantId == tenantId, cancellationToken)
               ?? throw new KeyNotFoundException($"Upload session {sessionId} was not found.");
    }

    /// <summary>
    /// Marks an expired session failed once, up front, instead of letting every remaining chunk
    /// discover the dead session URI on its own.
    /// </summary>
    private async Task<bool> FailIfExpiredAsync(UploadSession session, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (session.IsResumable(now)) return false;

        await FailAsync(
            session,
            $"This upload session expired at {session.ExpiresAt:u}. Start the upload again.",
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Fails a session once, and gives its reserved bytes back in the same breath.
    ///
    /// <para>The release is here rather than at each call site because every path that fails a
    /// session has to do it and the one that forgot would leave a tenant paying rent on an upload
    /// that never landed — invisible until they are inexplicably out of room. The guards above
    /// (<c>Status != InProgress</c> returns early) are what make it exactly once.</para>
    /// </summary>
    private async Task FailAsync(UploadSession session, string reason, CancellationToken cancellationToken)
    {
        session.Status = UploadSessionStatus.Failed;
        session.FailureReason = reason;

        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        await TenantStorageMeter.ReleaseAsync(
            db, session.TenantId, session.SizeBytes, cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static UploadProgress Describe(UploadSession session, Guid? storedFileId) =>
        new(session.Id, session.BytesConfirmed, session.SizeBytes, session.Status, storedFileId,
            session.FailureReason);

    private async Task<UploadProgress> DescribeAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        // A completed session names its file directly. It used to be recovered by matching tenant,
        // account, name and byte count, which was a guess that two uploads of the same file made
        // ambiguous; UploadSession.StoredFileId replaced it.
        if (session.StoredFileId is not { } storedFileId) return Describe(session, null);

        // Confirm it still exists: a customer can delete a file and then poll the finished session
        // that made it, and naming a soft-deleted row would have the panel offer a file it will
        // refuse to serve.
        var alive = await db.StoredFiles
            .AsNoTracking()
            .AnyAsync(
                f => f.Id == storedFileId && f.TenantId == session.TenantId && f.DeletedAt == null,
                cancellationToken);

        return Describe(session, alive ? storedFileId : null);
    }
}
