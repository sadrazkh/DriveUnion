using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The two numbers that decide whether a workspace may still serve a file: what it has put on the
/// wire this calendar month, and what its plan sold it.
///
/// <para><b>One round trip, on the hottest anonymous route in the product.</b> The allowance is a
/// column on <c>Tenants</c> and the spend is a sum over <c>TenantUsageDays</c>, and they are asked
/// for together as a projection with a correlated sub-select rather than as two awaits. A visitor
/// clicking a download link should not pay two latencies to find out that they may have the file.</para>
///
/// <para><b>The month is the same month everything else in the product means.</b> First to last day
/// of the calendar month in UTC, from the same <see cref="TimeProvider"/> <c>TrafficMeter</c> stamps
/// its rows with — so the figure this refuses on and the figure «پلن و مصرف» draws cannot come to
/// disagree about which downloads are in the window. A window computed in the server's local zone
/// would roll over three and a half hours early for a Tehran reader and a day late for the rows.</para>
///
/// <para>Not a <c>ITrafficMeter</c> method, for the reason <see cref="IEgressAllowance"/> gives: that
/// interface is on the write path of every download and has no business reading <c>Tenants</c>.</para>
/// </summary>
public sealed class EgressAllowanceReader(DriveUnionDbContext db, TimeProvider clock) : IEgressAllowance
{
    public async Task<EgressStanding> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = new DateOnly(today.Year, today.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);

        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.MonthlyEgressBytes,

                // Cast to long? and coalesced, because SUM over no rows is NULL in SQL and a
                // workspace that has served nothing this month is the ordinary case on the first of
                // the month. Left as a non-nullable long, EF materialises that NULL into a zero on
                // Postgres and throws on the way through SQLite.
                Spent = db.TenantUsageDays
                    .Where(u => u.TenantId == tenantId && u.Day >= from && u.Day <= to)
                    .Sum(u => (long?)u.EgressBytes) ?? 0L,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // No such workspace. Zeroes, which IsOverAllowance reads as over — see IEgressAllowance for
        // why that is the direction to be wrong in on a path that spends the operator's bandwidth.
        return row is null
            ? new EgressStanding(0, 0)
            : new EgressStanding(row.Spent, row.MonthlyEgressBytes);
    }
}
