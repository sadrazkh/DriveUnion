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

/// <summary>
/// What an operator typed into the tier form: a code, a name and the four numbers.
///
/// <para>An explicit request shape, never the <c>Plan</c> entity. <c>Id</c>, <c>SortOrder</c>,
/// <c>IsRetired</c> and <c>CreatedAt</c> are deliberately absent — order is moved by its own
/// command, retirement is its own command, and a row's identity and birthday are not an operator's
/// to post.</para>
/// </summary>
public sealed record PlanDraft(string Code, string Name, PlanNumbers Numbers);

/// <summary>Which way <see cref="IPlanCatalogueEditor.MoveAsync"/> takes a tier in the list.</summary>
public enum PlanMove
{
    Up,
    Down,
}

/// <summary>
/// One tier, and the two facts an operator needs before they touch it.
/// </summary>
/// <param name="WorkspacesOnPlan">
/// How many workspaces carry this tier as their label. <b>None of them changes when the tier is
/// edited</b>, which is the misunderstanding this figure exists to defuse — it is the size of the
/// re-apply, not the size of the edit.
/// </param>
/// <param name="WorkspacesHoldingOtherNumbers">
/// How many of those workspaces currently hold numbers that differ from the tier's. They are the
/// ones a re-apply would actually move — a workspace with a negotiated override is in this count,
/// and re-applying takes the override back.
/// </param>
/// <param name="IsConfiguredDefault">
/// True when <c>Plans:DefaultPlanCode</c> names this tier. It cannot be retired, deleted or
/// re-coded, because every new workspace is created on it.
/// </param>
public sealed record PlanUsage(
    PlanSummary Plan,
    int WorkspacesOnPlan,
    int WorkspacesHoldingOtherNumbers,
    bool IsConfiguredDefault);

/// <summary>
/// The whole catalogue as the operator's screen reads it, plus the one thing no single row can say.
/// </summary>
/// <param name="DefaultPlanCode">The configured <c>Plans:DefaultPlanCode</c>, verbatim.</param>
/// <param name="DefaultPlanExists">
/// False when the setting names no row. <c>TenantPlanService</c> throws <c>KeyNotFoundException</c>
/// in that state and the first person to find out is a customer whose sign-up 500s, so the screen
/// says it in words instead.
/// </param>
public sealed record PlanCatalogueState(
    IReadOnlyList<PlanUsage> Tiers,
    string DefaultPlanCode,
    bool DefaultPlanExists);

/// <summary>
/// <b>The only writer of the plan catalogue.</b> Separate from <see cref="IPlanCatalogueReader"/>
/// and from <see cref="ITenantPlanService"/> for the reason that file already gives: listing the
/// templates, editing the templates and moving a customer's ceiling are three authorities over two
/// tables, and one interface that did all of them would be reached for by the screen that only
/// needs the first.
///
/// <para><b>Nothing here writes a tenant's four columns.</b> Editing a tier changes no workspace —
/// the numbers were copied onto the tenant row when the tier was applied and nothing on any
/// enforcement path joins back. <see cref="ReapplyAsync"/> is the one command that reaches
/// workspaces, and it reaches them by calling <see cref="ITenantPlanService.SetTenantPlanAsync"/>
/// once per workspace, so every ceiling it moves still has its <c>TenantQuotaChange</c> row behind
/// it.</para>
///
/// <para>Every refusal is a <c>PlanEditRefusedException</c> carrying a <c>PlanEditRefusal</c>. None
/// of them carries a sentence: the wording is bilingual and belongs to the screen.</para>
/// </summary>
public interface IPlanCatalogueEditor
{
    /// <summary>Every tier with its workspace counts, and whether the configured default resolves.</summary>
    Task<PlanCatalogueState> StateAsync(CancellationToken cancellationToken);

    /// <summary>One tier and its counts. Null when no tier is coded that.</summary>
    Task<PlanUsage?> UsageAsync(string planCode, CancellationToken cancellationToken);

    /// <summary>
    /// A new tier, appended to the end of the list. It is live and assignable immediately; nothing
    /// is on it, so nothing can be disturbed by it.
    /// </summary>
    Task<PlanSummary> CreateAsync(PlanDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites a tier's code, name and four numbers.
    ///
    /// <para><b>This moves nobody.</b> Every workspace on the tier keeps the numbers it was given,
    /// and the screen says so where the operator is typing rather than afterwards.</para>
    /// </summary>
    Task<PlanSummary> EditAsync(string planCode, PlanDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Hides a tier from new assignment, or puts it back. Workspaces already on it keep working and
    /// keep their numbers, which is the whole payoff of copying rather than joining.
    /// </summary>
    Task<PlanSummary> SetRetiredAsync(string planCode, bool retired, CancellationToken cancellationToken);

    /// <summary>
    /// Swaps a tier with its neighbour in the list. A no-op at either end, because a button that
    /// refuses at the edge of a list is a refusal about nothing.
    /// </summary>
    Task<PlanSummary> MoveAsync(string planCode, PlanMove direction, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a tier no workspace is on — the one an operator created two minutes ago and mis-typed.
    ///
    /// <para>A tier a workspace is on is refused with <c>InUseCannotBeDeleted</c>. The database would
    /// refuse it too, <c>Tenant.PlanId</c> being a <c>Restrict</c> foreign key, but it would do it as
    /// a constraint violation on a screen; retirement is the answer and the refusal says so.</para>
    /// </summary>
    Task DeleteAsync(string planCode, CancellationToken cancellationToken);

    /// <summary>
    /// Copies this tier's numbers onto every workspace that is on it, in one transaction, writing
    /// one <c>TenantQuotaChange</c> per number that actually moves on each of them.
    ///
    /// <para>It is explicit and separate from <see cref="EditAsync"/> on purpose: an edit that
    /// silently moved a hundred paying customers' ceilings is the failure the copy-not-join design
    /// exists to prevent, and a bulk move with no per-tenant history behind it is exactly the
    /// question <c>TenantQuotaChange</c> was built to answer.</para>
    ///
    /// <para>It takes back negotiated overrides on the workspaces it touches — that is what
    /// "re-apply this tier" means, and <see cref="PlanUsage.WorkspacesHoldingOtherNumbers"/> is how
    /// the screen counts them before the operator confirms.</para>
    /// </summary>
    /// <returns>How many workspaces actually changed. Workspaces already holding the tier's numbers are written to and produce no history row.</returns>
    Task<int> ReapplyAsync(
        string planCode,
        string reason,
        Guid? changedByUserId,
        CancellationToken cancellationToken);
}
