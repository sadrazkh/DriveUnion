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
}
