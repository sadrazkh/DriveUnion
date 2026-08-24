using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// The sweeper's side, and it has no tenant anywhere in it.
///
/// <para>It runs with no request, no cookie and no principal, over rows that each carry their own
/// tenant. A tenant-scoped read from here would be handed <c>Guid.Empty</c> and would sweep nothing,
/// for ever, silently — which is the shape M1 §8 exists to protect against.</para>
/// </summary>
public sealed class TrashPurge(
    DriveUnionDbContext db,
    IDriveClient drive,
    TimeProvider clock,
    ILogger<TrashPurge> logger) : ITrashPurge
{
    public async Task<int> PurgeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var now = clock.GetUtcNow();

        // Two queries rather than one, on purpose. This first one reads a key and a date per waiting
        // file across every tenant, which is what the filtered index on PurgeAfter holds; the second
        // reads the rest for the handful that are actually due. Selecting the whole target in one
        // pass would pull every file id and folder in the trash into memory to throw most of them
        // away.
        //
        // The deadline is judged here rather than in SQL because SQLite will not compare a
        // DateTimeOffset — the same reason TelegramSweeperService gives for its own cutoffs — and
        // this runs on SQLite in the tests and Postgres in production.
        //
        // A null PurgeAfter is excluded by the query itself: those rows were deleted before the
        // trash existed and have no deadline, and guessing one would destroy somebody's file under
        // rules that did not exist when they deleted it. Emptying the trash is what takes them. The
        // DeletedAt half is belt and braces: only a deletion stamps a deadline, so a live row
        // carrying one is a fault somewhere else and not something to destroy a file over.
        var waiting = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PurgeAfter != null && f.DeletedAt != null)
            .Select(f => new { f.Id, f.PurgeAfter })
            .ToListAsync(cancellationToken);

        var due = waiting
            .Where(f => f.PurgeAfter <= now)
            .OrderBy(f => f.PurgeAfter)
            .Take(batchSize)
            .Select(f => f.Id)
            .ToList();

        if (due.Count == 0) return 0;

        var targets = await db.StoredFiles
            .AsNoTracking()
            .Where(f => due.Contains(f.Id))
            .Select(f => new PurgeTarget(f.Id, f.TenantId, f.GoogleAccountId, f.DriveFileId, f.SizeBytes))
            .ToListAsync(cancellationToken);

        return await TrashPurgeRunner.PurgeAsync(db, drive, logger, targets, cancellationToken);
    }
}
