using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Backup;

/// <summary>
/// What the operator can see about the catalogue's own backups, and the button that asks for one.
///
/// <para><b>Why a screen at all.</b> A backup nobody can confirm is a backup nobody trusts, and one
/// that has been failing quietly since March is worse than none — it is a plan somebody is relying
/// on. Everything the worker learns is on a row; this is the half that reads those rows back and
/// says which accounts are actually holding a copy right now.</para>
///
/// <para>No tenant anywhere in this class, like <c>AccountMigrations</c>: the pool is the operator's
/// and a snapshot spans every workspace at once.</para>
/// </summary>
public sealed class CatalogueSnapshots(DriveUnionDbContext db, TimeProvider clock) : ICatalogueSnapshots
{
    public async Task<IReadOnlyList<CatalogueSnapshotView>> RecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0) return [];

        var snapshots = await db.CatalogueSnapshots.AsNoTracking().ToListAsync(cancellationToken);
        if (snapshots.Count == 0) return [];

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset. Taken after the sort
        // rather than in the query for the same reason — a Take over an unordered set is a page of
        // whatever the database felt like.
        var page = snapshots
            .OrderByDescending(s => s.RequestedAt)
            .Take(limit)
            .ToList();

        var ids = page.ConvertAll(s => s.Id);

        var copies = await db.CatalogueSnapshotCopies
            .AsNoTracking()
            .Where(c => ids.Contains(c.SnapshotId))
            .ToListAsync(cancellationToken);

        // The account's own label and address, so the screen says «A2 · pool2@…» rather than a Guid.
        // An account that has since been disconnected and removed still has copies recorded against
        // it, and the row is at its most useful precisely then — hence the fallback rather than a
        // join that would drop it.
        var accounts = await db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new { a.Id, a.Label, a.Email })
            .ToListAsync(cancellationToken);

        var known = accounts.ToDictionary(a => a.Id);

        return
        [
            .. page.Select(s => new CatalogueSnapshotView(
                s.Id,
                s.Name,
                s.Status,
                s.ByHand,
                s.RequestedAt,
                s.FinishedAt,
                s.TenantCount,
                s.AccountCount,
                s.FolderCount,
                s.FileCount,
                s.EncryptionCount,
                s.SizeBytes,
                s.CopiesWanted,
                s.CopiesMade,
                s.FailureReason,
                [
                    .. copies
                        .Where(c => c.SnapshotId == s.Id)
                        .OrderBy(c => known.GetValueOrDefault(c.GoogleAccountId)?.Label ?? string.Empty, StringComparer.Ordinal)
                        .Select(c => new CatalogueSnapshotCopyView(
                            c.GoogleAccountId,
                            known.GetValueOrDefault(c.GoogleAccountId)?.Label ?? "—",
                            known.GetValueOrDefault(c.GoogleAccountId)?.Email ?? "—",
                            c.DriveFileId,
                            c.WrittenAt,
                            c.RemovedAt)),
                ])),
        ];
    }

    public async Task<DateTimeOffset?> NewestGoodAtAsync(CancellationToken cancellationToken)
    {
        // The same question CatalogueBackup asks itself, for a different purpose: there it decides
        // whether one is due, here it decides how alarmed the screen should be. Two callers, one
        // expression, and it is short enough that sharing it would cost more than repeating it.
        var landed = await db.CatalogueSnapshots
            .Where(s => s.Status == CatalogueSnapshotStatus.Completed)
            .Select(s => new { s.FinishedAt, s.RequestedAt })
            .ToListAsync(cancellationToken);

        // In memory, for the DateTimeOffset reason above.
        return landed.Count == 0 ? null : landed.Max(s => s.FinishedAt ?? s.RequestedAt);
    }

    public async Task<SnapshotRequestResult> RequestAsync(
        Guid? requestedByUserId,
        CancellationToken cancellationToken)
    {
        var queued = await db.CatalogueSnapshots.AnyAsync(
            s => s.Status == CatalogueSnapshotStatus.Pending
                || s.Status == CatalogueSnapshotStatus.Running,
            cancellationToken);

        // Refused rather than queued twice. This is the button a worried operator presses three
        // times, and three snapshots of the same rows minutes apart is three uploads and one answer.
        if (queued) return new SnapshotRequestResult(null, SnapshotRefusal.AlreadyQueued);

        var now = clock.GetUtcNow();

        var snapshot = new CatalogueSnapshot
        {
            Id = Guid.NewGuid(),

            // A name for the row to carry until the worker starts, which is when it is renamed for
            // the moment the rows were actually read. See CatalogueBackup.RunOnceAsync.
            Name = CatalogueSnapshotFormat.NameFor(now),
            Status = CatalogueSnapshotStatus.Pending,
            ByHand = true,
            RequestedByUserId = requestedByUserId,
            RequestedAt = now,
        };

        db.CatalogueSnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);

        return new SnapshotRequestResult(snapshot.Id, SnapshotRefusal.None);
    }
}
