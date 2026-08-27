using DriveUnion.Core.Application;

namespace DriveUnion.Tests.Fakes;

/// <summary>
/// An <see cref="ITrafficMeter"/> that keeps what it was told instead of writing it.
///
/// <para>The interesting question about metering on the download path is «what number reached it,
/// for whose workspace» — a database would be a second thing to read to find that out. What is
/// asserted against a real <c>TrafficMeter</c> is the arithmetic, in <c>TrafficMeterTests</c>.</para>
/// </summary>
public sealed class RecordingTrafficMeter : ITrafficMeter
{
    private readonly List<(Guid TenantId, long Bytes)> _recorded = [];

    public IReadOnlyList<(Guid TenantId, long Bytes)> Recorded => _recorded;

    /// <summary>Everything counted for one workspace, which is what a single transfer's test wants.</summary>
    public long BytesFor(Guid tenantId) => _recorded.Where(r => r.TenantId == tenantId).Sum(r => r.Bytes);

    public Task RecordAsync(Guid tenantId, long bytes, CancellationToken cancellationToken)
    {
        _recorded.Add((tenantId, bytes));

        return Task.CompletedTask;
    }

    public Task<UsageTotal> MonthAsync(Guid tenantId, DateOnly anyDayInIt, CancellationToken cancellationToken) =>
        Task.FromResult(new UsageTotal(BytesFor(tenantId), _recorded.Count(r => r.TenantId == tenantId)));

    public Task<IReadOnlyList<UsageDay>> RangeAsync(
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UsageDay>>([]);

    public Task<IReadOnlyDictionary<Guid, UsageTotal>> EveryTenantMonthAsync(
        DateOnly anyDayInIt,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UsageTotal>>(
            _recorded
                .GroupBy(r => r.TenantId)
                .ToDictionary(g => g.Key, g => new UsageTotal(g.Sum(r => r.Bytes), g.Count())));

    /// <summary>
    /// Empty, like <see cref="RangeAsync"/> above and for the same reason: this double keeps what it
    /// was told and not when it was told it, so it has no day to put a row on. What draws a chart
    /// from real rows is tested over a real <c>TrafficMeter</c>, which is where the grouping lives.
    /// </summary>
    public Task<IReadOnlyList<UsageDay>> EveryTenantRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UsageDay>>([]);
}
