using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// <b>The only writer of a tenant's four effective limits.</b>
///
/// <para>Assigning a plan <i>copies</i> its numbers onto the tenant row. Nothing here — and nothing
/// anywhere on an enforcement path — joins a check to <c>Plan</c>. Editing a template changes nobody
/// until somebody re-applies it, and <see cref="SetTenantPlanAsync"/> is the whole of "somebody
/// re-applies it".</para>
///
/// <para>Every field that moves writes a <see cref="TenantQuotaChange"/> row in the same
/// <c>SaveChanges</c> as the move. A cap that changed with no row behind it is a support conversation
/// with no answer, which is the failure the history table exists to prevent.</para>
/// </summary>
public sealed class TenantPlanService(
    DriveUnionDbContext db,
    TimeProvider clock,
    IOptions<PlansOptions> options) : ITenantPlanService
{
    public async Task<TenantPlanView> SetTenantPlanAsync(
        Guid tenantId,
        string planCode,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var tenant = await RequireTenantAsync(tenantId, cancellationToken);

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == planCode, cancellationToken)
            ?? throw new KeyNotFoundException($"No plan is coded '{planCode}'.");

        // Retirement hides a plan from new assignment and leaves every tenant on it working, because
        // their numbers are on their own row. Re-applying it to a tenant that is already on it is
        // therefore still legal — that is how an edit reaches them — and moving somebody onto it is
        // not.
        if (plan.IsRetired && tenant.PlanId != plan.Id)
        {
            throw new InvalidOperationException(
                $"Plan '{planCode}' is retired and cannot be assigned to a tenant that is not on it.");
        }

        var before = Numbers(tenant);
        var previousCode = await PlanCodeOfAsync(tenant.PlanId, cancellationToken);

        Record(tenant, previousCode, plan.Code, before, plan.Numbers, reason, changedByUserId);

        Apply(tenant, plan.Numbers);
        tenant.PlanId = plan.Id;
        tenant.PlanAppliedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(tenant, cancellationToken);
    }

    public async Task<TenantPlanView> SetTenantQuotaOverrideAsync(
        Guid tenantId,
        QuotaField field,
        long value,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        // A byte cap read from a form is an int away from being nonsense, and the seat count is an
        // int column. Refusing here beats writing a value the column cannot hold.
        if (field == QuotaField.MaxMembers && value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"A seat count of {value} is not a number of people.");
        }

        if (!Enum.IsDefined(field))
        {
            // The comparison `field == something` is fail-open on a value nobody defined, so an enum
            // that arrived from a form or a bad migration is refused rather than matched by default.
            throw new ArgumentOutOfRangeException(nameof(field), $"{field} is not a quota dimension.");
        }

        var tenant = await RequireTenantAsync(tenantId, cancellationToken);

        var before = Numbers(tenant);
        var after = With(before, field, value);
        var code = await PlanCodeOfAsync(tenant.PlanId, cancellationToken);

        // The plan code is unchanged on both sides on purpose: an override does not take a customer
        // off their tier, it moves one of their numbers. The tier is still what the operator's screen
        // and the customer's card name.
        Record(tenant, code, code, before, after, reason, changedByUserId);

        Apply(tenant, after);

        await db.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(tenant, cancellationToken);
    }

    public Task<TenantPlanView> SetTenantStorageQuotaAsync(
        Guid tenantId,
        long bytes,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken) =>
        SetTenantQuotaOverrideAsync(
            tenantId, QuotaField.StorageBytes, bytes, reason, changedByUserId, cancellationToken);

    public async Task<TenantPlanView> ApplyDefaultPlanAsync(
        Guid tenantId,
        Guid? changedByUserId,
        CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);

        // Idempotent, and deliberately not "re-apply the default": a tenant that already carries a
        // plan may have a negotiated override on it, and a second run of this at start-up must not
        // quietly undo what somebody sold.
        if (tenant.PlanId is not null) return await DescribeAsync(tenant, cancellationToken);

        return await SetTenantPlanAsync(
            tenantId,
            options.Value.DefaultPlanCode,
            DefaultPlanReason,
            changedByUserId,
            cancellationToken);
    }

    public async Task<TenantPlanView?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        return tenant is null ? null : await DescribeAsync(tenant, cancellationToken);
    }

    public async Task<IReadOnlyList<QuotaChangeEntry>> HistoryAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await db.TenantQuotaChanges
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Ordered in memory rather than in SQL: SQLite stores a DateTimeOffset as text and will not
        // order one the way Postgres does, and a history that reads newest-first on the box and
        // oldest-first under the tests is a screen nothing can pin.
        return
        [
            .. rows
                .OrderByDescending(c => c.ChangedAt)
                .Select(c => new QuotaChangeEntry(
                    c.ChangedAt,
                    c.ChangedByUserId,
                    c.PlanCodeBefore,
                    c.PlanCodeAfter,
                    c.Field,
                    c.OldValue,
                    c.NewValue,
                    c.Reason)),
        ];
    }

    public async Task<DowngradePreview?> PreviewPlanAsync(
        Guid tenantId,
        string planCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null) return null;

        var plan = await db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == planCode, cancellationToken);

        if (plan is null) return null;

        var proposed = plan.Numbers;

        // Files larger than the proposed per-file limit are counted and left alone. The limit is on
        // the act of uploading, not on possession: a stored file keeps downloading and keeps sharing,
        // because anything else means a pricing change hides customer data.
        var filesOver = await db.StoredFiles
            .AsNoTracking()
            .CountAsync(
                f => f.TenantId == tenantId && f.DeletedAt == null && f.SizeBytes > proposed.MaxFileBytes,
                cancellationToken);

        var membersUsed = await MemberCountAsync(tenantId, cancellationToken);

        return new DowngradePreview(
            tenant.Id,
            tenant.Name,
            Numbers(tenant),
            proposed,
            tenant.StorageUsedBytes,
            Math.Max(0, tenant.StorageUsedBytes - proposed.StorageBytes),
            filesOver,
            Math.Max(0, membersUsed - proposed.MaxMembers));
    }

    /// <summary>
    /// The reason written when a tenant is given the configured default. It names the setting rather
    /// than a person, because no person did it.
    /// </summary>
    internal const string DefaultPlanReason = "Plans:DefaultPlanCode applied to a new workspace.";

    private async Task<Tenant> RequireTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
        ?? throw new KeyNotFoundException($"Tenant {tenantId} does not exist.");

    private async Task<string?> PlanCodeOfAsync(Guid? planId, CancellationToken cancellationToken)
    {
        if (planId is not { } id) return null;

        return await db.Plans
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.Code)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// One history row per field that actually moved, and none for a field that did not.
    ///
    /// <para>Re-applying an unchanged plan therefore writes nothing, which is what makes the table
    /// readable: a page of rows saying "500 GB became 500 GB" is a page nobody scrolls past to find
    /// the change they came for.</para>
    /// </summary>
    private void Record(
        Tenant tenant,
        string? planCodeBefore,
        string? planCodeAfter,
        PlanNumbers before,
        PlanNumbers after,
        string reason,
        Guid? changedByUserId)
    {
        var now = clock.GetUtcNow();

        foreach (var (field, oldValue, newValue) in Differences(before, after))
        {
            db.TenantQuotaChanges.Add(new TenantQuotaChange
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ChangedAt = now,
                ChangedByUserId = changedByUserId,
                PlanCodeBefore = planCodeBefore,
                PlanCodeAfter = planCodeAfter,
                Field = field,
                OldValue = oldValue,
                NewValue = newValue,
                Reason = reason,
            });
        }
    }

    private static IEnumerable<(QuotaField Field, long Old, long New)> Differences(
        PlanNumbers before,
        PlanNumbers after)
    {
        if (before.StorageBytes != after.StorageBytes)
        {
            yield return (QuotaField.StorageBytes, before.StorageBytes, after.StorageBytes);
        }

        if (before.MaxFileBytes != after.MaxFileBytes)
        {
            yield return (QuotaField.MaxFileBytes, before.MaxFileBytes, after.MaxFileBytes);
        }

        if (before.MonthlyEgressBytes != after.MonthlyEgressBytes)
        {
            yield return (QuotaField.MonthlyEgressBytes, before.MonthlyEgressBytes, after.MonthlyEgressBytes);
        }

        if (before.MaxMembers != after.MaxMembers)
        {
            yield return (QuotaField.MaxMembers, before.MaxMembers, after.MaxMembers);
        }
    }

    private static PlanNumbers Numbers(Tenant tenant) => new(
        tenant.StorageQuotaBytes,
        tenant.MaxFileBytes,
        tenant.MonthlyEgressBytes,
        tenant.MaxMembers);

    private static PlanNumbers With(PlanNumbers numbers, QuotaField field, long value) => field switch
    {
        QuotaField.StorageBytes => numbers with { StorageBytes = value },
        QuotaField.MaxFileBytes => numbers with { MaxFileBytes = value },
        QuotaField.MonthlyEgressBytes => numbers with { MonthlyEgressBytes = value },
        QuotaField.MaxMembers => numbers with { MaxMembers = (int)value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>
    /// The copy itself. Four assignments, in one place, and the only four in the codebase — a test
    /// reads the source to keep that true.
    /// </summary>
    private static void Apply(Tenant tenant, PlanNumbers numbers)
    {
        tenant.StorageQuotaBytes = numbers.StorageBytes;
        tenant.MaxFileBytes = numbers.MaxFileBytes;
        tenant.MonthlyEgressBytes = numbers.MonthlyEgressBytes;
        tenant.MaxMembers = numbers.MaxMembers;
    }

    private async Task<TenantPlanView> DescribeAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var plan = tenant.PlanId is { } id
            ? await db.Plans.AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { p.Code, p.Name })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var fileCount = await db.StoredFiles
            .AsNoTracking()
            .CountAsync(f => f.TenantId == tenant.Id && f.DeletedAt == null, cancellationToken);

        return new TenantPlanView(
            tenant.Id,
            tenant.Name,
            plan?.Code,
            plan?.Name,
            tenant.PlanAppliedAt,
            Numbers(tenant),
            tenant.StorageUsedBytes,
            fileCount,
            await MemberCountAsync(tenant.Id, cancellationToken));
    }

    /// <summary>
    /// Seats in use. Pending invitations are not counted here because there is no invitation table
    /// yet; when there is one, they have to be — an acceptance that overshoots the seat limit is the
    /// bug counting them prevents, exactly as in-flight sessions are counted against storage.
    /// </summary>
    private async Task<int> MemberCountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().CountAsync(u => u.TenantId == tenantId, cancellationToken);
}
