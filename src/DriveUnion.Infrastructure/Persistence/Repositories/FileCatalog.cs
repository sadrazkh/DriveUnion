using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Trash;
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
/// <param name="trash">
/// Where a deleted file goes. Optional only so that a harness building this class by hand is not
/// made to supply a Drive it has no use for — <c>AddDriveUnionTrash</c> registers one, so nothing in
/// the running application ever sees it absent. Without it a delete is the soft delete alone, which
/// is what this class did before the trash existed.
/// </param>
public sealed class FileCatalog(
    DriveUnionDbContext db,
    TimeProvider clock,
    ITrashMover? trash = null) : IFileCatalog
{
    public async Task<IReadOnlyList<FileListItem>> ListAsync(
        Guid tenantId,
        FileListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rows = db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null);

        if (filter.TagId is { } tagId)
        {
            // An EXISTS rather than a join, so a file carrying the tag appears once however the join
            // table grows. The tenant is on the join row as well as on the file — see FileTag for
            // why a scope goes missing in exactly this kind of query.
            rows = rows.Where(f => db.FileTags.Any(t =>
                t.StoredFileId == f.Id && t.TagId == tagId && t.TenantId == tenantId));
        }

        if (filter.Term is { } term)
        {
            // The folder is not applied here. A search is the whole workspace — see the contract for
            // why — and the screen says where each hit lives instead of hiding the ones that are
            // somewhere else.
            // Folded on both sides rather than compared with the database's own idea of case.
            //
            // `Name.Contains(term)` is what reads naturally and it is the trap: EF turns it into a
            // LIKE, and LIKE is case-sensitive on Postgres and case-insensitive on SQLite. The suite
            // runs on SQLite and production runs on Postgres, so searching «report» for
            // «Report-Q3.pdf» would pass every test and find nothing for a customer.
            //
            // `ToLower()` is `lower()` on both providers, which is the one form that means the same
            // thing in both. Its limit, stated because it is real: SQLite's `lower()` folds ASCII
            // only, so on the test provider an accented capital does not match its lowercase. Both
            // of this product's languages are unaffected — Persian has no case, and a file name in
            // English is ASCII — and Postgres folds properly, so the limit lives in the tests rather
            // than in what a customer gets.
            //
            // Still `Contains` rather than a hand-built pattern: EF parameterises it and escapes the
            // wildcards, so a file called «100% done» is searchable and «%» finds it rather than
            // everything.
            var folded = term.ToLowerInvariant();

            rows = rows.Where(f => f.Name.ToLower().Contains(folded));
        }

        if (!filter.IsWorkspaceWide)
        {
            rows = rows.Where(f => f.FolderId == filter.FolderId);
        }

        var files = await rows
            .Select(f => new FileListItem(
                f.Id,
                f.Name,
                f.MimeType,
                f.SizeBytes,
                f.ModifiedAt,
                // Revoked links drop out of the count; expiry and the download cap are per-link
                // states the detail panel evaluates with ShareLink.Evaluate, and duplicating that
                // rule in SQL would be two places to get it wrong.
                db.ShareLinks.Count(l => l.StoredFileId == f.Id && l.IsActive),
                f.FolderId))
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

    /// <summary>
    /// Deleting a file moves it to the trash and leaves the tenant's counter alone.
    ///
    /// <para>The quota is freed on purge, not here, by the owner's decision: until the purge runs
    /// those bytes are genuinely still occupying the operator's pool, and this is the only version
    /// where the number on the customer's screen and the bytes on the disk agree. It is also what
    /// Drive itself does, so it is the model the customer already has. A customer who wants the
    /// space now empties the trash now, which is a button that really frees it.</para>
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Read first, and only to learn where the bytes physically are. It decides nothing about
        // permission: the UPDATE below still carries the tenant predicate, so another tenant's file
        // is not "found and rejected" there either — and a file that is not matched here never
        // reaches Drive at all.
        //
        // The window between this read and that write costs at most a redundant move: a delete that
        // raced another one finds affected == 0 and answers false, having moved a file that was
        // going to the same folder anyway.
        var file = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new
            {
                f.GoogleAccountId,
                f.DriveFileId,
                f.DriveFolderId,
                f.OwnerUserId,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null) return false;

        // Drive first, then the row — the same order the purge uses, and for the same reason. A move
        // that lands over a row that does not leaves the file live in the panel and sitting in the
        // trash folder, and pressing delete again puts it right. A row stamped over a move that
        // never happened leaves the bytes in the customer's home folder with nothing left to retry,
        // because the file is already deleted and cannot be deleted twice.
        var placement = trash is null
            ? null
            : await trash.ToTrashAsync(
                tenantId,
                file.GoogleAccountId,
                file.OwnerUserId,
                file.DriveFileId,
                file.DriveFolderId,
                cancellationToken);

        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        var affected = await db.StoredFiles
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.DeletedAt, now),
                cancellationToken);

        if (affected == 0) return false;

        if (placement is not null)
        {
            // Where it is now, where it came from, and when the sweeper may take it. The deadline is
            // stamped from the retention window in force at this moment and never consulted again
            // for this file, so lowering the setting tomorrow shortens the wait for what is deleted
            // then and cannot reach back to what somebody deleted today expecting a month.
            await db.StoredFiles
                .Where(f => f.Id == fileId && f.TenantId == tenantId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(f => f.DriveFolderId, placement.TrashFolderId)
                        .SetProperty(f => f.RestoreFolderId, placement.RestoreFolderId)
                        .SetProperty(f => f.PurgeAfter, placement.PurgeAfter),
                    cancellationToken);
        }

        // Deleting the file revokes its links in the same breath. Leaving them active would let
        // /d/{slug} keep answering for a file the tenant removed, and "revoked" is the honest thing
        // for the owner's panel to show afterwards. Restoring the file does not undo this.
        await db.ShareLinks
            .Where(l => l.StoredFileId == fileId && l.TenantId == tenantId && l.IsActive)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.IsActive, false),
                cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
