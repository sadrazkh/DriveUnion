using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// Browser → OVH → Google, one chunk at a time.
///
/// The request body is handed to <see cref="IDriveClient"/> as the very stream this class received.
/// Nothing here reads it, measures it, or copies it: a 96 GB upload spooled to memory or disk is a
/// 96 GB bug, and it is the kind that only shows up on the one file big enough to matter.
/// </summary>
public sealed class UploadCoordinator(
    DriveUnionDbContext db,
    IDriveClient drive,
    IUploadTargetSelector targetSelector,
    TimeProvider clock) : IUploadCoordinator
{
    /// <summary>The <c>DriveUnion/</c> folder each account gets; tenants are folders inside it.</summary>
    private const string RootFolderName = "DriveUnion";

    /// <summary>A browser that sends no type still gets a file, not a rejection.</summary>
    private const string FallbackMimeType = "application/octet-stream";

    public async Task<BeginUploadResult> BeginAsync(
        Guid tenantId,
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

        var tenantSlug = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} does not exist.");

        var accountId = await targetSelector.SelectAsync(request.SizeBytes, cancellationToken)
            ?? throw new UploadRejectedException(
                $"No connected Google account can accept a file of {request.SizeBytes} bytes.");

        var account = await db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            ?? throw new UploadRejectedException(
                $"Google account {accountId} was selected for this upload but no longer exists.");

        // The root folder is created once per account and remembered; the tenant folder is asked for
        // every time because find-or-create is what the Drive client does anyway and there is
        // nowhere in the model to cache a per-tenant, per-account id.
        account.RootFolderId ??= await drive.EnsureFolderAsync(
            accountId, RootFolderName, null, cancellationToken);

        var tenantFolderId = await drive.EnsureFolderAsync(
            accountId, tenantSlug, account.RootFolderId, cancellationToken);

        var mimeType = string.IsNullOrWhiteSpace(request.MimeType) ? FallbackMimeType : request.MimeType;

        var driveSession = await drive.BeginResumableUploadAsync(
            accountId,
            new DriveUploadRequest(request.FileName, mimeType, request.SizeBytes, tenantFolderId),
            cancellationToken);

        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
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

        session.BytesConfirmed = outcome.ConfirmedLength;

        Guid? storedFileId = null;
        if (outcome.Completed is { } metadata)
        {
            var stored = new StoredFile
            {
                Id = Guid.NewGuid(),
                TenantId = session.TenantId,
                GoogleAccountId = session.GoogleAccountId,
                DriveFileId = metadata.FileId,
                Name = metadata.Name,
                MimeType = metadata.MimeType,
                SizeBytes = metadata.SizeBytes,
                CreatedAt = metadata.CreatedTime,
                ModifiedAt = metadata.ModifiedTime,
            };

            db.StoredFiles.Add(stored);
            session.Status = UploadSessionStatus.Completed;
            storedFileId = stored.Id;
        }

        // The file row and the session's new state are one SaveChanges: a completed upload with no
        // StoredFile is a file the customer paid to send and cannot see.
        await db.SaveChangesAsync(cancellationToken);

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

    private async Task FailAsync(UploadSession session, string reason, CancellationToken cancellationToken)
    {
        session.Status = UploadSessionStatus.Failed;
        session.FailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static UploadProgress Describe(UploadSession session, Guid? storedFileId) =>
        new(session.Id, session.BytesConfirmed, session.SizeBytes, session.Status, storedFileId,
            session.FailureReason);

    private async Task<UploadProgress> DescribeAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        var storedFileId = session.Status == UploadSessionStatus.Completed
            ? await FindStoredFileIdAsync(session, cancellationToken)
            : null;

        return Describe(session, storedFileId);
    }

    /// <summary>
    /// Recovers the file a finished session produced.
    ///
    /// <see cref="UploadSession"/> carries no StoredFileId and <see cref="StoredFile"/> carries no
    /// session id, so the only handle left is the shape of what was uploaded. Tenant, account, name
    /// and exact byte count together are ambiguous only between two uploads of the very same file,
    /// where either answer names the same bytes.
    /// </summary>
    private async Task<Guid?> FindStoredFileIdAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        var candidates = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == session.TenantId
                        && f.GoogleAccountId == session.GoogleAccountId
                        && f.Name == session.FileName
                        && f.SizeBytes == session.SizeBytes
                        && f.DeletedAt == null)
            .Select(f => new { f.Id, f.CreatedAt })
            .ToListAsync(cancellationToken);

        // Newest wins, chosen in memory because SQLite will not ORDER BY a DateTimeOffset — see
        // FileCatalog.ListAsync. There is normally exactly one row here.
        return candidates.Count == 0 ? null : candidates.MaxBy(f => f.CreatedAt)!.Id;
    }
}
