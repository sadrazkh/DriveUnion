using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// The operator's catalogue, read-only.
///
/// <para>It has no tenant argument and never will: a plan carries no <c>TenantId</c>, and this is the
/// list of templates rather than the list of anybody's limits.</para>
/// </summary>
public sealed class PlanCatalogueReader(DriveUnionDbContext db) : IPlanCatalogueReader
{
    public async Task<IReadOnlyList<PlanSummary>> ListAsync(
        bool includeRetired,
        CancellationToken cancellationToken)
    {
        var rows = await db.Plans
            .AsNoTracking()
            .Where(p => includeRetired || !p.IsRetired)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Code)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Summarise)];
    }

    public async Task<PlanSummary?> FindAsync(string planCode, CancellationToken cancellationToken)
    {
        var row = await db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == planCode, cancellationToken);

        return row is null ? null : Summarise(row);
    }

    private static PlanSummary Summarise(Plan plan) =>
        new(plan.Id, plan.Code, plan.Name, plan.Numbers, plan.IsRetired, plan.SortOrder);
}
