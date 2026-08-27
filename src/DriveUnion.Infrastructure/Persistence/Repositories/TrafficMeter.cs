using DriveUnion.Core.Application;
using DriveUnion.Core.Metering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The egress counter, written once per delivered download and read by three screens.
/// </summary>
public sealed class TrafficMeter(
    DriveUnionDbContext db,
    TimeProvider clock,
    ILogger<TrafficMeter> logger) : ITrafficMeter
{
    public async Task RecordAsync(Guid tenantId, long bytes, CancellationToken cancellationToken)
    {
        // Zero is not nothing: a download counted is a download counted, and a HEAD or a range that
        // returned no body still happened. But a negative is a bug upstream and writing it would
        // corrupt the month rather than report it.
        if (bytes < 0) bytes = 0;

        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        try
        {
            // The update first, because after a workspace's first download of the day it is the only
            // statement that runs. Two round trips on one request a day, one on every other.
            var updated = await db.TenantUsageDays
                .Where(u => u.TenantId == tenantId && u.Day == day)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(u => u.EgressBytes, u => u.EgressBytes + bytes)
                        .SetProperty(u => u.Downloads, u => u.Downloads + 1),
                    cancellationToken);

            if (updated > 0) return;

            db.TenantUsageDays.Add(new TenantUsageDay
            {
                TenantId = tenantId,
                Day = day,
                EgressBytes = bytes,
                Downloads = 1,
            });

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two downloads landing on the same workspace's first byte of the day race to insert the
            // same key, and one of them loses. Retried once as an update, which is what it would
            // have been a millisecond later.
            db.ChangeTracker.Clear();

            var retried = await db.TenantUsageDays
                .Where(u => u.TenantId == tenantId && u.Day == day)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(u => u.EgressBytes, u => u.EgressBytes + bytes)
                        .SetProperty(u => u.Downloads, u => u.Downloads + 1),
                    cancellationToken);

            if (retried == 0)
            {
                // Losing a count is worse than a log line and better than a 500 on a file the
                // visitor already has. See ITrafficMeter.RecordAsync.
                logger.LogWarning(exception, "A download's traffic was not counted for tenant {TenantId}.", tenantId);
            }
        }
    }

    public async Task<UsageTotal> MonthAsync(
        Guid tenantId,
        DateOnly anyDayInIt,
        CancellationToken cancellationToken)
    {
        var (from, to) = MonthOf(anyDayInIt);

        // Summed in SQL, which is the whole reason Day is a DateOnly: this is a BETWEEN over at most
        // thirty-one rows on both providers, and the same question over DownloadEvent.OccurredAt
        // could not be asked in SQL at all.
        var rows = await db.TenantUsageDays
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Day >= from && u.Day <= to)
            .GroupBy(_ => 1)
            .Select(g => new { Bytes = g.Sum(u => u.EgressBytes), Downloads = g.Sum(u => u.Downloads) })
            .FirstOrDefaultAsync(cancellationToken);

        return rows is null ? UsageTotal.Nothing : new UsageTotal(rows.Bytes, rows.Downloads);
    }

    public async Task<IReadOnlyList<UsageDay>> RangeAsync(
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (to < from) return [];

        return await db.TenantUsageDays
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Day >= from && u.Day <= to)
            .OrderBy(u => u.Day)
            .Select(u => new UsageDay(u.Day, u.EgressBytes, u.Downloads))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, UsageTotal>> EveryTenantMonthAsync(
        DateOnly anyDayInIt,
        CancellationToken cancellationToken)
    {
        var (from, to) = MonthOf(anyDayInIt);

        // No tenant predicate, and this is the one method in the product that legitimately has none:
        // it is the operator's view of every workspace, and it returns totals rather than anything
        // that could name a file. Its callers are behind DriveUnionPolicies.Operator.
        var rows = await db.TenantUsageDays
            .AsNoTracking()
            .Where(u => u.Day >= from && u.Day <= to)
            .GroupBy(u => u.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Bytes = g.Sum(u => u.EgressBytes),
                Downloads = g.Sum(u => u.Downloads),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.TenantId, r => new UsageTotal(r.Bytes, r.Downloads));
    }

    public async Task<IReadOnlyList<UsageDay>> EveryTenantRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (to < from) return [];

        // No tenant predicate, and the second method in this product that legitimately has none. It
        // groups the workspaces away rather than listing them: what comes back is a day and two
        // quantities, so there is nothing in the result that could name a customer even if a screen
        // wanted to print one. Its caller is behind DriveUnionPolicies.Operator.
        //
        // Grouped and ordered in SQL, which is the whole reason Day is a DateOnly. The same question
        // over DownloadEvent.OccurredAt could not be asked in SQL at all — SQLite keeps a
        // DateTimeOffset as text and will neither compare nor ORDER BY one — and this layer runs on
        // SQLite in the tests and Postgres in production.
        //
        // Bounded by the window rather than by the customer count: at most one row per day comes
        // back, whatever the read cost inside the database, so the operator's home page does not get
        // slower as customers are added.
        // An anonymous type and not UsageDay directly, which is the same shape EveryTenantMonthAsync
        // uses two methods up and for the same reason: EF will translate a grouped projection into
        // an anonymous type and refuses to translate one into a record's constructor.
        var rows = await db.TenantUsageDays
            .AsNoTracking()
            .Where(u => u.Day >= from && u.Day <= to)
            .GroupBy(u => u.Day)
            .Select(g => new
            {
                Day = g.Key,
                Bytes = g.Sum(u => u.EgressBytes),
                Downloads = g.Sum(u => u.Downloads),
            })
            .ToListAsync(cancellationToken);

        // Ordered here rather than in the query. It is a sort over at most one row per day of the
        // window — thirty of them for the caller this exists for — and ordering a grouped projection
        // is the other half of what EF would not translate. Nothing about it is the DateTimeOffset
        // problem: Day is a DateOnly precisely so the WHERE above can happen in SQL on both providers.
        return
        [
            .. rows
                .OrderBy(r => r.Day)
                .Select(r => new UsageDay(r.Day, r.Bytes, r.Downloads)),
        ];
    }

    /// <summary>The first and last day of the calendar month a date falls in.</summary>
    private static (DateOnly From, DateOnly To) MonthOf(DateOnly day)
    {
        var from = new DateOnly(day.Year, day.Month, 1);

        return (from, from.AddMonths(1).AddDays(-1));
    }
}
