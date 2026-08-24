using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// One file the purge is about to destroy. It carries its own <c>TenantId</c> because the sweeper
/// has none: it runs with no request and no principal, and the row is the only place the tenant can
/// come from.
/// </summary>
internal sealed record PurgeTarget(
    Guid Id,
    Guid TenantId,
    Guid GoogleAccountId,
    string DriveFileId,
    long SizeBytes);

/// <summary>
/// The one place a file is actually destroyed, shared by the customer's empty-trash button and the
/// sweeper so that the order below exists once.
///
/// <para><b>The order is load-bearing.</b> Drive first, then the release and the row. A byte gone
/// from Drive and still counted costs the customer an upload; a byte still in Drive and no longer
/// counted costs the operator a pool, and it is invisible until the pool is full. So every failure
/// is arranged to land in the first half — which is also the half a retry can climb out of, because
/// Drive treats deleting a file that is already gone as success and the next sweep simply finishes
/// what this one started.</para>
/// </summary>
internal static class TrashPurgeRunner
{
    /// <summary>
    /// Destroys each target in turn and returns how many actually went.
    ///
    /// <para>One file's failure is not the batch's: the row and its bytes are left exactly as they
    /// were and the next sweep picks it up again, so a single unreachable file cannot stop the trash
    /// behind it from ever being emptied. A rate limit is different and stops the batch, because
    /// past that point the housekeeping is spending the request budget the customers are uploading
    /// with.</para>
    /// </summary>
    public static async Task<int> PurgeAsync(
        DriveUnionDbContext db,
        IDriveClient drive,
        ILogger logger,
        IReadOnlyList<PurgeTarget> targets,
        CancellationToken cancellationToken)
    {
        var purged = 0;

        foreach (var target in targets)
        {
            try
            {
                await PurgeOneAsync(db, drive, target, cancellationToken);
                purged++;
            }
            catch (OperationCanceledException)
            {
                // The host is stopping or the request went away. What is already purged is purged,
                // and the rest is still due.
                throw;
            }
            catch (DriveRateLimitedException ex)
            {
                logger.LogWarning(
                    ex,
                    "The purge stopped early after {Purged} file(s): Drive is rate limiting, and the "
                    + "remaining budget belongs to uploads.",
                    purged);

                break;
            }
            catch (Exception ex)
            {
                // Deliberately broad. This runs over rows nobody is watching, and one file that
                // cannot be deleted — a revoked account, a hand-deleted folder, a transport fault —
                // must not become a trash that never empties again.
                logger.LogWarning(
                    ex,
                    "File {FileId} could not be purged and will be tried again.",
                    target.Id);
            }
        }

        return purged;
    }

    private static async Task PurgeOneAsync(
        DriveUnionDbContext db,
        IDriveClient drive,
        PurgeTarget target,
        CancellationToken cancellationToken)
    {
        // Drive first, and nothing in the database has moved yet. A failure here leaves the row in
        // the trash with its bytes still counted against the tenant, which is the truth: they are
        // still occupying the operator's pool.
        await drive.DeleteAsync(target.GoogleAccountId, target.DriveFileId, cancellationToken);

        // Then the release and the row, together. Apart, a crash between them either counts bytes
        // against a customer whose row is gone — nothing left to find them by — or drops the count
        // while a second attempt at the row releases the same bytes twice.
        //
        // A failure of this transaction leaves the bytes gone from Drive and still counted, which is
        // the recoverable half: the next sweep finds the same row, Drive answers the delete of an
        // already-deleted file with success, and the release and the drop happen then.
        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        await TenantStorageMeter.ReleaseAsync(db, target.TenantId, target.SizeBytes, cancellationToken);

        // The row goes rather than staying soft-deleted. There is nothing left for it to describe,
        // and a trash listing built from DeletedAt would otherwise keep showing a file whose bytes
        // no longer exist. Its links and their download events go with it, by the cascades declared
        // in DriveUnionDbContext.
        await db.StoredFiles
            .Where(f => f.Id == target.Id)
            .ExecuteDeleteAsync(cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}
