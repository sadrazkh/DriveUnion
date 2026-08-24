namespace DriveUnion.Core.Plans;

/// <summary>
/// The four numbers a tier sells, as one value.
///
/// They travel together because assigning a plan is one act — see <see cref="Plan"/> for why it is a
/// copy — and a method that took four longs in a row is a method whose arguments get transposed.
/// </summary>
/// <param name="StorageBytes">The ceiling on stored bytes. M5 §7's <c>StorageQuotaBytes</c>.</param>
/// <param name="MaxFileBytes">The ceiling on one file, enforced in <c>IUploadCoordinator.BeginAsync</c>.</param>
/// <param name="MonthlyEgressBytes">The traffic allowance. Carried by P1, metered by P2.</param>
/// <param name="MaxMembers">Seats. Carried by P1, enforced at invitation creation by P3.</param>
public readonly record struct PlanNumbers(
    long StorageBytes,
    long MaxFileBytes,
    long MonthlyEgressBytes,
    int MaxMembers);

/// <summary>
/// The operator's catalogue of tiers.
///
/// <para><b>There is no <c>TenantId</c> here and there must never be one.</b> A plan belongs to the
/// operator the way <c>GoogleAccount</c> and <c>TelegramBotSettings</c> do, and it is reachable only
/// from the operator surface.</para>
///
/// <para><b>A plan is a template, not a foreign key that anything reads at enforcement time.</b>
/// Assigning one copies <see cref="Numbers"/> onto the <c>Tenant</c> row; no upload, download or
/// invitation ever joins to this table. Editing a row here changes nothing for any existing tenant
/// until somebody re-applies it, and that is the design rather than an oversight:</para>
/// <list type="number">
/// <item>A negotiated override — 3 TB on a 1 TB tier — is the normal shape of selling to businesses.
/// Once the tenant row has to be able to hold a number that differs from its plan, a mixed model
/// where some limits come from the plan and some from the tenant is the worst of both, and every
/// read has to know which is which.</item>
/// <item>M5 §10 already left exactly one seam: a tenant's cap has one writer. If the enforcement path
/// read through a plan instead, that seam would have been designed for nothing and a customer's
/// ceiling would have two writers.</item>
/// <item>A cap is a promise to one customer, so «چرا سهمیه‌ام کم شد» has to be answerable with a row
/// naming a person, a time and a reason. A template edit cannot produce that per tenant, and a
/// pricing experiment that silently moves a paying customer's ceiling is how their uploads start
/// failing on a Tuesday.</item>
/// </list>
///
/// <para>There is deliberately <b>no <c>Price</c> column</b>. It is the single column that would turn
/// this into a billing table, and a price with no engine behind it is a number on a screen that
/// nobody honours. When money is scoped, the price lives with the thing that charges it.</para>
/// </summary>
public sealed class Plan
{
    public Guid Id { get; set; }

    /// <summary>
    /// The stable handle an operator and a configuration file use — <c>starter</c>, <c>standard</c>.
    /// Unique, and never rendered to a customer, who sees <see cref="Name"/>.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>What the operator calls this tier. Free text, renameable, and not a key.</summary>
    public required string Name { get; set; }

    public long StorageBytes { get; set; }

    public long MaxFileBytes { get; set; }

    public long MonthlyEgressBytes { get; set; }

    public int MaxMembers { get; set; }

    /// <summary>
    /// Hidden from new assignment; every tenant already on it keeps working, because their numbers
    /// live on their own row. That is one of the payoffs of copying rather than joining.
    /// </summary>
    public bool IsRetired { get; set; }

    /// <summary>The order the operator's list is drawn in. Not derived from any of the numbers.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public PlanNumbers Numbers => new(StorageBytes, MaxFileBytes, MonthlyEgressBytes, MaxMembers);

    /// <summary>
    /// The four numbers, written as one act — the same reason <see cref="PlanNumbers"/> exists at
    /// all: four longs assigned in a row is four longs that get transposed.
    ///
    /// <para>It is a method rather than a setter on <see cref="Numbers"/> because a settable
    /// property would become a mapped one, and EF has no column for a record struct. A method is
    /// invisible to the model and changes no schema.</para>
    ///
    /// <para><b>Writing this changes no workspace.</b> These are the template's numbers; a tenant's
    /// live on the tenant's own row and are moved only by <c>ITenantPlanService</c>.</para>
    /// </summary>
    public void SetNumbers(PlanNumbers numbers)
    {
        StorageBytes = numbers.StorageBytes;
        MaxFileBytes = numbers.MaxFileBytes;
        MonthlyEgressBytes = numbers.MonthlyEgressBytes;
        MaxMembers = numbers.MaxMembers;
    }
}
