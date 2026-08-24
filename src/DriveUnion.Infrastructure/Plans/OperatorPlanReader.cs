using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// Usage across every tenant, and what the operator has promised against what the pool actually
/// holds.
///
/// <para>Aggregates only. There is no method here that returns a file row, and there must not be:
/// the operator inspecting one customer's files does it through the same tenant-scoped repository a
/// customer's own request would call, with the tenantId from the route.</para>
///
/// <para>It takes no tenant argument at all — not even a nullable one meaning "all of them". A
/// nullable tenantId on a scoped method is one null reference away from being every customer's
/// default, which is the failure this codebase has already paid for once.</para>
/// </summary>
public sealed class OperatorPlanReader(DriveUnionDbContext db) : IOperatorPlanReader
{
    public async Task<OperatorPlanOverview> OverviewAsync(CancellationToken cancellationToken)
    {
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.PlanId,
                t.StorageQuotaBytes,
                t.StorageUsedBytes,
                t.MaxFileBytes,
                t.MonthlyEgressBytes,
                t.MaxMembers,
            })
            .ToListAsync(cancellationToken);

        var planCodes = await db.Plans
            .AsNoTracking()
            .Select(p => new { p.Id, p.Code })
            .ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);

        var fileCounts = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.DeletedAt == null)
            .GroupBy(f => f.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        var memberCounts = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId != null)
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        // The pool is what the connected accounts hold. A disconnected account is not capacity —
        // nothing can be written to it — so counting it would make the commitment figure flatter
        // than the truth on exactly the day the operator most needs it to be right.
        var pool = await db.GoogleAccounts
            .AsNoTracking()
            .Where(a => a.Status != GoogleAccountStatus.Disconnected)
            .SumAsync(a => a.QuotaTotalBytes, cancellationToken);

        var rows = tenants
            .Select(t => new OperatorTenantPlanRow(
                t.Id,
                t.Name,
                t.PlanId is { } id && planCodes.TryGetValue(id, out var code) ? code : null,
                new PlanNumbers(t.StorageQuotaBytes, t.MaxFileBytes, t.MonthlyEgressBytes, t.MaxMembers),
                t.StorageUsedBytes,
                fileCounts.TryGetValue(t.Id, out var files) ? files : 0,
                memberCounts.TryGetValue(t.Id, out var members) ? members : 0))
            .ToList();

        return new OperatorPlanOverview(
            rows,

            // sum(Tenant.StorageQuotaBytes) — deliberately not a query over the plan catalogue. The
            // figure has to be right for a tenant on a retired plan and for one carrying a negotiated
            // override, and both of those are true only because the numbers live on the tenant's row.
            rows.Sum(r => r.Limits.StorageBytes),
            pool,
            rows.Sum(r => r.Limits.MonthlyEgressBytes));
    }
}
