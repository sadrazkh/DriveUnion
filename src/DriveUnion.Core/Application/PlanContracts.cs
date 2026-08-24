using DriveUnion.Core.Plans;

namespace DriveUnion.Core.Application;

/// <summary>One row of the operator's catalogue, as a screen reads it.</summary>
public sealed record PlanSummary(
    Guid Id,
    string Code,
    string Name,
    PlanNumbers Numbers,
    bool IsRetired,
    int SortOrder);

/// <summary>
/// One tenant's effective limits and what it has actually spent.
///
/// <para><see cref="PlanCode"/> is a label for the operator's screen and for the customer's own
/// card. Nothing compares against it — the four numbers in <see cref="Limits"/> are the tenant's
/// own, copied from a template that may since have been edited or retired.</para>
///
/// <para>There is no traffic figure here. The meter, its window and its counting stream are P2's,
/// and rendering a zero for a customer who has been serving downloads all month would be a lie the
/// panel tells with a straight face.</para>
/// </summary>
public sealed record TenantPlanView(
    Guid TenantId,
    string TenantName,
    string? PlanCode,
    string? PlanName,
    DateTimeOffset? PlanAppliedAt,
    PlanNumbers Limits,
    long StorageUsedBytes,
    int FileCount,
    int MembersUsed)
{
    /// <summary>
    /// Storage spent as a percentage of the cap, clamped so an over-cap tenant cannot overflow a
    /// progress track. A cap of zero reads as full rather than as a division by zero.
    /// </summary>
    public double StoragePercent => Limits.StorageBytes <= 0
        ? 100d
        : Math.Clamp(StorageUsedBytes * 100d / Limits.StorageBytes, 0d, 100d);

    /// <summary>
    /// True when the tenant is at or past its storage cap — the state a downgrade produces
    /// deliberately and an upload produces accidentally. Uploads stop; nothing else does.
    /// </summary>
    public bool IsOverStorage => StorageUsedBytes >= Limits.StorageBytes;
}

/// <summary>One entry of the tenant's quota history, newest first on the operator's page.</summary>
public sealed record QuotaChangeEntry(
    DateTimeOffset ChangedAt,
    Guid? ChangedByUserId,
    string? PlanCodeBefore,
    string? PlanCodeAfter,
    QuotaField Field,
    long OldValue,
    long NewValue,
    string Reason);

/// <summary>
/// What a downgrade would actually do, shown before it is confirmed.
///
/// <para>The rule it renders is one line and it covers all four dimensions: <b>a downgrade
/// constrains the next action, never an existing one.</b> Nothing is deleted, no member is removed,
/// no stored file becomes unreachable because it is now larger than the new per-file limit — that
/// limit is on the act of uploading, not on possession.</para>
///
/// <para>An operator who downgrades a customer without seeing this is going to hear about it from
/// the customer.</para>
/// </summary>
/// <param name="StorageOverageBytes">
/// How far past the new cap the tenant would immediately be. Zero when they still fit.
/// </param>
/// <param name="FilesOverNewFileLimit">
/// How many stored files are larger than the new per-file limit. <b>They keep working.</b> The
/// figure is here because "would this break their existing files" is the first question an operator
/// asks, and the honest answer — none of them — is only convincing with a number beside it.
/// </param>
/// <param name="MembersOverNewSeatLimit">
/// How many seats over the new limit the tenant would be. Existing members are not removed; new
/// invitations are refused until the count fits.
/// </param>
public sealed record DowngradePreview(
    Guid TenantId,
    string TenantName,
    PlanNumbers Current,
    PlanNumbers Proposed,
    long StorageUsedBytes,
    long StorageOverageBytes,
    int FilesOverNewFileLimit,
    int MembersOverNewSeatLimit)
{
    /// <summary>True when any dimension of the proposal is below what the tenant already holds.</summary>
    public bool ProducesAnOverage =>
        StorageOverageBytes > 0 || FilesOverNewFileLimit > 0 || MembersOverNewSeatLimit > 0;
}

/// <summary>
/// The operator's cross-tenant view: aggregates only, never file rows across tenants.
/// </summary>
/// <param name="CommittedStorageBytes">
/// <c>sum(Tenant.StorageQuotaBytes)</c> — the same query it always was, unaffected by anything
/// happening in the plan catalogue, which is one of the payoffs of copying rather than joining.
/// </param>
/// <param name="PoolStorageBytes">
/// What the connected Google accounts actually hold between them.
/// <b>Over-commitment is allowed and displayed rather than prevented</b>: caps are per-customer
/// ceilings, not reservations, and requiring the sum to fit would make every new sign-up wait on a
/// capacity purchase.
/// </param>
/// <param name="SoldMonthlyEgressBytes">
/// The sum of monthly traffic allowances, marked <i>sold</i> rather than <i>reserved</i>. It gets no
/// pool comparison: the operator's egress ceiling is a bandwidth number, not a stored quantity, and
/// nobody yet knows what this box's uplink can do. The <i>actual</i> figure beside it is P2's.
/// </param>
public sealed record OperatorPlanOverview(
    IReadOnlyList<OperatorTenantPlanRow> Tenants,
    long CommittedStorageBytes,
    long PoolStorageBytes,
    long SoldMonthlyEgressBytes)
{
    public bool IsOverCommitted => CommittedStorageBytes > PoolStorageBytes;
}

/// <summary>One tenant on the operator's list. Aggregates, and no file ever.</summary>
public sealed record OperatorTenantPlanRow(
    Guid TenantId,
    string TenantName,
    string? PlanCode,
    PlanNumbers Limits,
    long StorageUsedBytes,
    int FileCount,
    int MembersUsed);

/// <summary>
/// The operator's catalogue, read-only. Separate from <see cref="ITenantPlanService"/> because
/// listing the templates and moving a customer's ceiling are different authorities over different
/// rows, and one interface that did both would be reached for by the screen that only needs the
/// first.
/// </summary>
public interface IPlanCatalogueReader
{
    /// <param name="includeRetired">
    /// Retired plans are excluded from new assignment and included in a list that has to explain a
    /// tenant already on one.
    /// </param>
    Task<IReadOnlyList<PlanSummary>> ListAsync(bool includeRetired, CancellationToken cancellationToken);

    Task<PlanSummary?> FindAsync(string planCode, CancellationToken cancellationToken);
}

/// <summary>
/// <b>The only writer of a tenant's effective limits.</b>
///
/// <para>M5 §10 left exactly one seam — a tenant's storage cap has one command behind it, callable
/// only from the operator surface. This widens that seam by one command and no more, and it holds
/// the same property for all four dimensions rather than only for storage: nothing else in the
/// codebase assigns <c>Tenant.StorageQuotaBytes</c>, <c>MaxFileBytes</c>, <c>MonthlyEgressBytes</c>
/// or <c>MaxMembers</c>, and a test reads the source to keep it that way.</para>
///
/// <para>Every write produces a <see cref="QuotaChangeEntry"/>. A billing system, if one is ever
/// scoped, becomes a second caller of these two commands and reads meters that already exist for
/// other reasons. Nothing else has to exist for money to attach.</para>
///
/// <para><c>tenantId</c> is an argument on every method, never ambient. On the operator's screens it
/// comes from the route and is handed to the same method a customer's own request would call: there
/// is deliberately no unscoped overload and no nullable tenantId meaning "all tenants".</para>
/// </summary>
public interface ITenantPlanService
{
    /// <summary>
    /// Copies a plan's four numbers onto the tenant, writing one history row per field that moved.
    ///
    /// <para>Re-applying the plan a tenant is already on is how an edited template reaches them, and
    /// it is the only way: nothing does it automatically. A tenant already on a retired plan may be
    /// re-applied; a tenant may not be moved <i>onto</i> one.</para>
    ///
    /// <para>Upgrades take effect immediately and leave in-flight uploads alone, because those
    /// already reserved. Downgrades are the same write: the lower number is stored and the tenant may
    /// be over it, which is a state the product already has.</para>
    /// </summary>
    Task<TenantPlanView> SetTenantPlanAsync(
        Guid tenantId,
        string planCode,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one number on one tenant, without taking them off their plan.
    ///
    /// <para>Overrides exist because a negotiated customer is the normal case, and a product that
    /// cannot express one forces the operator to invent a fake plan per customer. The override lives
    /// on the tenant row where the plan's numbers already are, so nothing on any enforcement path
    /// learns that overrides exist at all.</para>
    /// </summary>
    Task<TenantPlanView> SetTenantQuotaOverrideAsync(
        Guid tenantId,
        QuotaField field,
        long value,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// M5 §10's command, kept by name and now the storage-shaped special case of
    /// <see cref="SetTenantQuotaOverrideAsync"/> rather than a third writer.
    /// </summary>
    Task<TenantPlanView> SetTenantStorageQuotaAsync(
        Guid tenantId,
        long bytes,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies <c>Plans:DefaultPlanCode</c> to a tenant that has never had a plan applied.
    ///
    /// <para>Idempotent, and a no-op for a tenant that already carries one — re-running it must not
    /// undo a negotiated override. This is the one setting a new customer's numbers come from; M5
    /// §8's <c>Tenancy:DefaultStorageQuotaBytes</c> is replaced by it rather than joined to it,
    /// because two keys that can disagree about what a new customer gets is a bug waiting for the
    /// day they do.</para>
    /// </summary>
    Task<TenantPlanView> ApplyDefaultPlanAsync(
        Guid tenantId,
        Guid? changedByUserId,
        CancellationToken cancellationToken);

    /// <summary>Null when there is no such tenant.</summary>
    Task<TenantPlanView?> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The tenant's quota history, newest first. Empty for a tenant nobody has touched.</summary>
    Task<IReadOnlyList<QuotaChangeEntry>> HistoryAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// What applying <paramref name="planCode"/> would leave the tenant holding. Reads only; the
    /// screen shows it before the operator confirms.
    /// </summary>
    Task<DowngradePreview?> PreviewPlanAsync(
        Guid tenantId,
        string planCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Usage across every tenant, and the commitment against the real pool.
///
/// <para>Sessionless by nature — it is asked from an operator's request today and would be asked
/// from a report tomorrow — so it takes no tenant argument at all rather than a nullable one meaning
/// "all of them". A nullable tenantId on a scoped method is one null reference away from being every
/// customer's default.</para>
/// </summary>
public interface IOperatorPlanReader
{
    Task<OperatorPlanOverview> OverviewAsync(CancellationToken cancellationToken);
}
