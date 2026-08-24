using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// One row per update, and the reason a webhook retry does not cost a customer a duplicate file.
///
/// <para>The claim is an insert whose failure is the answer. Reading first and inserting second would
/// be a race with exactly the shape this table exists to close: two deliveries of the same update
/// arriving together both find nothing, both insert, and both upload. The unique key is the arbiter,
/// and a <see cref="DbUpdateException"/> from it means "somebody else has this one" rather than an
/// error worth reporting.</para>
/// </summary>
public sealed class TelegramUpdateLedger(DriveUnionDbContext db, TimeProvider clock) : ITelegramUpdateLedger
{
    /// <summary>
    /// A week. Telegram keeps an undelivered update for 24 hours, so anything older than that cannot
    /// arrive again; the extra six days are for a clock that is wrong rather than for a redelivery.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public async Task<bool> TryClaimAsync(long updateId, CancellationToken cancellationToken)
    {
        var row = new TelegramUpdateSeen
        {
            UpdateId = updateId,
            ReceivedAt = clock.GetUtcNow(),
        };

        db.TelegramUpdatesSeen.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            // Detached the moment it is written, so the database's key stays the arbiter. A context
            // that kept tracking it would refuse a second claim in its own identity map — an
            // in-process answer that looks the same and is not, because it would not be there after
            // the restart or on the other worker that the redelivery actually arrives at.
            db.Entry(row).State = EntityState.Detached;

            return true;
        }
        catch (DbUpdateException)
        {
            // The primary key refused it, which is this method working rather than failing. The
            // tracker has to be cleared or the rejected row is retried by the next SaveChanges on
            // this context — which is the one that writes the outbox item.
            db.ChangeTracker.Clear();

            return false;
        }
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - Retention;

        // Read the timestamps and judge them here. SQLite stores a DateTimeOffset as text and will
        // not compare one, which is the same reason the link-token sweep and the public link reader
        // both keep their date predicates out of SQL.
        var doomed = await db.TelegramUpdatesSeen
            .AsNoTracking()
            .Select(u => new { u.UpdateId, u.ReceivedAt })
            .ToListAsync(cancellationToken);

        var ids = doomed.Where(u => u.ReceivedAt < cutoff).Select(u => u.UpdateId).ToList();

        if (ids.Count == 0) return 0;

        return await db.TelegramUpdatesSeen
            .Where(u => ids.Contains(u.UpdateId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
