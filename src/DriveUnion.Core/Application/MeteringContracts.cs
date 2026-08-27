namespace DriveUnion.Core.Application;

/// <param name="EgressBytes">Bytes put on the wire across the period.</param>
/// <param name="Downloads">Counted downloads across the period.</param>
public sealed record UsageTotal(long EgressBytes, int Downloads)
{
    public static readonly UsageTotal Nothing = new(0, 0);
}

/// <summary>One day of a workspace's usage, for drawing a run of them.</summary>
public sealed record UsageDay(DateOnly Day, long EgressBytes, int Downloads);

/// <summary>
/// What a workspace has spent, and the only thing in this product that knows.
///
/// <para>Until this existed the panel showed «— / ۵۰۰ GB» on the capacity card and on both
/// dashboards: the workspace row carried the allowance its plan sells and nothing counted what was
/// used against it. Three screens said so in a sentence, which was honest and is not a feature.</para>
///
/// <para><c>tenantId</c> is explicit here as everywhere else. <see cref="RecordAsync"/> is the one
/// call on the download path, and it is deliberately the only write: metering that needed a second
/// call to stay correct would be metering that is wrong whenever somebody forgets the second call.</para>
/// </summary>
public interface ITrafficMeter
{
    /// <summary>
    /// Adds one download's bytes to today.
    ///
    /// <para>Called once per delivered transfer, on the way out — including the aborted ones, which
    /// cost the operator exactly as much as the finished ones. It must not throw into the response
    /// path: a download that was served is served whether or not the counter took, and an exception
    /// here would turn somebody's finished file into a 500.</para>
    /// </summary>
    Task RecordAsync(Guid tenantId, long bytes, CancellationToken cancellationToken);

    /// <summary>Everything spent in the calendar month <paramref name="anyDayInIt"/> falls in.</summary>
    Task<UsageTotal> MonthAsync(Guid tenantId, DateOnly anyDayInIt, CancellationToken cancellationToken);

    /// <summary>
    /// The days from <paramref name="from"/> to <paramref name="to"/> inclusive that have anything
    /// on them, oldest first. Days with no traffic are absent rather than present and zero.
    /// </summary>
    Task<IReadOnlyList<UsageDay>> RangeAsync(
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);

    /// <summary>Every workspace's spend for the month, for the operator's screens. Keyed by tenant.</summary>
    Task<IReadOnlyDictionary<Guid, UsageTotal>> EveryTenantMonthAsync(
        DateOnly anyDayInIt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every workspace's spend, one row per day, oldest first — the operator's egress over time.
    ///
    /// <para>Summed across workspaces rather than keyed by one, which is what makes it the operator's
    /// figure and not a customer's: what comes back is a day and a quantity, and there is nothing in
    /// it that could name a workspace, a file or an account. Its callers are behind
    /// <c>DriveUnionPolicies.Operator</c>, like <see cref="EveryTenantMonthAsync"/>'s.</para>
    ///
    /// <para>It exists because neither of the two readers above can answer «what has the whole
    /// product served, day by day»: <see cref="RangeAsync"/> is per workspace, and
    /// <see cref="EveryTenantMonthAsync"/> collapses the month into one figure per workspace. Adding
    /// up thirty calls to the first would mean one query per customer on the operator's home page.</para>
    ///
    /// <para>Days with no traffic anywhere are absent rather than present and zero, which is the same
    /// contract <see cref="RangeAsync"/> keeps. A caller drawing a chart fills the gaps — and it has
    /// to, because a chart with a missing column is a chart that lies about which day is which.</para>
    /// </summary>
    Task<IReadOnlyList<UsageDay>> EveryTenantRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);
}

/// <summary>
/// A workspace's traffic standing: what it has served this calendar month, and what it is allowed to.
///
/// <para>The comparison lives here rather than at each of the three call sites that need it — the
/// gate on the public download path, the customer's own «پلن و مصرف» card, and whatever asks next.
/// Two spellings of «over the allowance» is one spelling too many for a rule that decides whether a
/// stranger gets a file.</para>
/// </summary>
/// <param name="SpentBytes">
/// Egress this calendar month, from <see cref="ITrafficMeter.MonthAsync"/> — the bytes that actually
/// reached visitors, counted as the response body was copied.
/// </param>
/// <param name="AllowanceBytes">
/// <c>Tenant.MonthlyEgressBytes</c>: the figure a plan put on the workspace's own row when it was
/// applied. Nothing joins back to the plan template, so an operator's negotiated override is already
/// in this number.
/// </param>
public sealed record EgressStanding(long SpentBytes, long AllowanceBytes)
{
    /// <summary>
    /// True once the month's bytes have reached the allowance.
    ///
    /// <para><c>&gt;=</c> and not <c>&gt;</c>, which is the same edge <c>TenantPlanView.IsOverStorage</c>
    /// draws: an allowance of exactly what has been spent has nothing left in it. It also means an
    /// allowance of zero is over from the first byte, which is what a zero has to mean — a workspace
    /// sold no traffic is a workspace that serves none, and reading zero as «unlimited» would make
    /// the emptiest possible row the most generous one in the product.</para>
    /// </summary>
    public bool IsOverAllowance => SpentBytes >= AllowanceBytes;
}

/// <summary>
/// Whether a workspace may still put bytes on the wire.
///
/// <para><b>Why this is not a method on <see cref="ITrafficMeter"/>.</b> That interface counts and
/// reports; this one compares a count against a ceiling that lives on a different table. Folding it
/// in would give the meter a reason to read <c>Tenants</c>, and the meter is on the write path of
/// every download in the product.</para>
///
/// <para><c>tenantId</c> is an explicit argument here as everywhere else. The one caller that matters
/// is anonymous — <c>/d/{slug}/file</c> has no principal at all — and it takes the workspace off the
/// ticket the slug resolved to, which is the answer to the lookup rather than a parameter of it.</para>
/// </summary>
public interface IEgressAllowance
{
    /// <summary>
    /// What the workspace has spent this month against what it may.
    ///
    /// <para>Zeroes for a workspace that is not there, which reads as <c>IsOverAllowance</c> and so
    /// refuses. That is the safe direction on a path that costs the operator money: a tenant row
    /// missing behind a live file is a fault, and serving unmetered egress until somebody notices is
    /// the wrong way to be wrong about one.</para>
    /// </summary>
    Task<EgressStanding> ReadAsync(Guid tenantId, CancellationToken cancellationToken);
}
