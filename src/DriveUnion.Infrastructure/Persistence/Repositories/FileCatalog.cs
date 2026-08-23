using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The tenant's own files, read from our rows rather than listed live from Drive.
///
/// Every query here carries <c>tenantId</c> in its <c>WHERE</c> clause because there is no global
/// query filter in this model and there must not be one — see the comment in
/// <see cref="DriveUnionDbContext.OnModelCreating"/>. Nothing projected out of this class mentions
/// <c>GoogleAccountId</c>: the customer must never learn which account holds their file.
/// </summary>
public sealed class FileCatalog(DriveUnionDbContext db, TimeProvider clock) : IFileCatalog
{
    public async Task<IReadOnlyList<FileListItem>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var files = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new FileListItem(
                f.Id,
                f.Name,
                f.MimeType,
                f.SizeBytes,
                f.ModifiedAt,
                // Revoked links drop out of the count; expiry and the download cap are per-link
                // states the detail panel evaluates with ShareLink.Evaluate, and duplicating that
                // rule in SQL would be two places to get it wrong.
                db.ShareLinks.Count(l => l.StoredFileId == f.Id && l.IsActive)))
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL because SQLite refuses ORDER BY on a DateTimeOffset — its
        // TEXT encoding does not sort correctly once two rows carry different offsets — and this
        // layer runs on SQLite in the tests and Postgres in production. M1 has no paging, so the
        // rows are all in hand already and sorting them costs nothing that the query did not.
        return [.. files.OrderByDescending(f => f.ModifiedAt)];
    }

    public async Task<FileDetail?> GetAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await db.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null,
                cancellationToken);

        if (file is null) return null;

        var links = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.StoredFileId == fileId && l.TenantId == tenantId)
            .Select(l => new LinkRow(
                l.Id, l.Slug, l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.IsActive, l.CreatedAt))
            .ToListAsync(cancellationToken);

        return new FileDetail(
            file.Id,
            file.Name,
            file.MimeType,
            file.SizeBytes,
            file.CreatedAt,
            file.ModifiedAt,
            // Sorted in memory for the reason given in ListAsync: SQLite will not ORDER BY a
            // DateTimeOffset, and one file's links are a handful of rows either way.
            [.. links.OrderByDescending(l => l.CreatedAt).Select(l => l.ToSummary())]);
    }

    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        // The tenant predicate lives in the UPDATE itself rather than in a read before it: another
        // tenant's file is then not "found and rejected", it is simply not matched, and there is no
        // window between the check and the write.
        var affected = await db.StoredFiles
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.DeletedAt, now),
                cancellationToken);

        if (affected == 0) return false;

        // Deleting the file revokes its links in the same breath. Leaving them active would let
        // /d/{slug} keep answering for a file the tenant removed, and "revoked" is the honest thing
        // for the owner's panel to show afterwards.
        await db.ShareLinks
            .Where(l => l.StoredFileId == fileId && l.TenantId == tenantId && l.IsActive)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.IsActive, false),
                cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
