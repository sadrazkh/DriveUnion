using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Tenancy;

/// <summary>
/// What the operator's workspace screens read.
///
/// <para><b>Aggregates and members, and no file row ever.</b> There is no method here that returns
/// one, and there must not be: an operator inspecting a customer's files does it through the same
/// tenant-scoped catalogue the customer's own request calls, with the tenant from the route. A
/// separately named reader that could only ever return counts is the shape that keeps that true when
/// somebody adds a screen in a hurry.</para>
///
/// <para>Every query is written out rather than filtered ambiently. This product has no global query
/// filter and must not acquire one: <c>/d/{slug}</c> is anonymous, a filter would hand it
/// <c>Guid.Empty</c>, and every live link in the product would start rendering «این لینک پیدا نشد».</para>
/// </summary>
public sealed class OperatorTenantDirectory(DriveUnionDbContext db, TimeProvider clock)
    : IOperatorTenantDirectory
{
    public async Task<IReadOnlyList<TenantListing>> ListAsync(CancellationToken cancellationToken)
    {
        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.PlanId,
                t.StorageQuotaBytes,
                t.StorageUsedBytes,
                t.MaxMembers,
                t.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        // Three grouped reads rather than three correlated sub-selects per row: the list is the
        // operator's home page for this part of the product and it grows one row per customer, so
        // the query count has to stay flat as customers arrive.
        var planCodes = await db.Plans
            .AsNoTracking()
            .Select(p => new { p.Id, p.Code })
            .ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);

        var memberCounts = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId != null)
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        // Live files only. A soft-deleted row is a file the customer has already stopped seeing and
        // whose bytes are pending purge from Drive; counting it here would make the list disagree
        // with the customer's own file table for as long as a purge is retrying.
        var fileCounts = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.DeletedAt == null)
            .GroupBy(f => f.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);

        return
        [
            .. tenants.Select(t => new TenantListing(
                t.Id,
                t.Name,
                t.Slug,
                t.PlanId is { } id && planCodes.TryGetValue(id, out var code) ? code : null,
                memberCounts.TryGetValue(t.Id, out var members) ? members : 0,
                t.MaxMembers,
                t.StorageUsedBytes,
                t.StorageQuotaBytes,
                fileCounts.TryGetValue(t.Id, out var files) ? files : 0,
                t.CreatedAt)),
        ];
    }

    public async Task<TenantWorkspaceView?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.Name, t.Slug, t.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null) return null;

        var now = clock.GetUtcNow();

        var members = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.LockoutEnabled,
                u.LockoutEnd,
                u.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return new TenantWorkspaceView(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.CreatedAt,
            [
                .. members.Select(u => new TenantMemberListing(
                    u.Id,

                    // Identity allows a null address; this product does not create one, because the
                    // panel signs in by email. An empty string rather than a throw: an operator
                    // looking at a row somebody inserted by hand needs to see it, not a 500.
                    u.Email ?? string.Empty,
                    u.DisplayName,

                    // Both halves, because Identity refuses a sign-in only when both are true. A row
                    // with a lockout end in the future and LockoutEnabled false signs in perfectly
                    // well, and a screen that called it «غیرفعال» would be lying in the direction
                    // that matters.
                    u.LockoutEnabled && u.LockoutEnd > now,
                    u.LockoutEnd,
                    u.CreatedAt)),
            ]);
    }
}
