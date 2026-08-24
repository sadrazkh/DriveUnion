using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// The customer's trash: what is in it, putting something back, and emptying it.
///
/// <para>Every query carries <c>tenantId</c> in its <c>WHERE</c>, because there is no global query
/// filter in this model and there must not be one — see the comment in
/// <see cref="DriveUnionDbContext.OnModelCreating"/>. Nothing projected out of here mentions the
/// Google account holding the bytes.</para>
/// </summary>
public sealed class TrashService(
    DriveUnionDbContext db,
    IDriveClient drive,
    ILogger<TrashService> logger) : ITrash
{
    public async Task<IReadOnlyList<TrashItem>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt != null)
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.SizeBytes,
                f.DeletedAt,
                f.PurgeAfter,
            })
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL for the reason FileCatalog.ListAsync gives: SQLite refuses
        // ORDER BY on a DateTimeOffset, and this layer runs on SQLite in the tests and Postgres in
        // production.
        return
        [
            .. rows
                .OrderByDescending(r => r.DeletedAt)
                .Select(r => new TrashItem(r.Id, r.Name, r.SizeBytes, r.DeletedAt!.Value, r.PurgeAfter)),
        ];
    }

    public async Task<bool> RestoreAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt != null)
            .Select(f => new
            {
                f.GoogleAccountId,
                f.DriveFileId,
                f.DriveFolderId,
                f.RestoreFolderId,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Not this tenant's, not in the trash, or the purge reached it first — all three are the
        // same answer, and deliberately: telling them apart would tell a caller that somebody else's
        // file id exists.
        if (file is null) return false;

        var destination = file.RestoreFolderId;

        if (!string.IsNullOrEmpty(destination))
        {
            // Drive first, then the row, the same way the delete and the purge do it. A move that
            // succeeds and a row that does not leaves the file back in its home folder while the
            // panel still shows it in the trash, and pressing restore again puts that right.
            await drive.MoveAsync(
                file.GoogleAccountId,
                file.DriveFileId,
                file.DriveFolderId,
                destination,
                cancellationToken);
        }

        // No restore folder means the row was soft-deleted before the trash existed: nothing ever
        // moved it, so it is still in the folder it was uploaded to and clearing the deletion is the
        // whole of putting it back.
        var folderNow = string.IsNullOrEmpty(destination) ? file.DriveFolderId : destination;

        var affected = await db.StoredFiles
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt != null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.DeletedAt, (DateTimeOffset?)null)
                    .SetProperty(f => f.DriveFolderId, folderNow)
                    .SetProperty(f => f.RestoreFolderId, (string?)null)
                    .SetProperty(f => f.PurgeAfter, (DateTimeOffset?)null),
                cancellationToken);

        // The file's links stay revoked. Deleting it revoked them, and a restore is not an
        // un-revoking: /d/{slug} answering again for a link the owner watched die would be a
        // surprise nobody asked for, and minting a new link is one press.
        return affected > 0;
    }

    public async Task<int> EmptyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Everything in this tenant's trash, deadline or not. A row with no PurgeAfter was deleted
        // before the trash existed and the sweeper leaves it alone rather than inventing a deadline
        // for it — but the customer pressing this button is the decision the sweeper refuses to
        // make, and it is the only thing that ever gives those bytes back.
        var targets = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt != null)
            .Select(f => new PurgeTarget(f.Id, f.TenantId, f.GoogleAccountId, f.DriveFileId, f.SizeBytes))
            .ToListAsync(cancellationToken);

        if (targets.Count == 0) return 0;

        var purged = await TrashPurgeRunner.PurgeAsync(db, drive, logger, targets, cancellationToken);

        logger.LogInformation(
            "A tenant emptied its trash: {Purged} of {Waiting} file(s) destroyed.",
            purged,
            targets.Count);

        return purged;
    }

    public Task<long> SizeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt != null)
            .SumAsync(f => f.SizeBytes, cancellationToken);
}
