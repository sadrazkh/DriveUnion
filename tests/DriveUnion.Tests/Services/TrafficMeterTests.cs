using DriveUnion.Core.Metering;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The egress counter: the thing that turned «— / ۵۰۰ GB» on three screens into a figure.
///
/// <para>A roll-up per workspace per day rather than a sum over <c>DownloadEvent</c>, and the reason
/// is in <see cref="TenantUsageDay"/>: that table is indexed on a <c>DateTimeOffset</c>, and SQLite
/// will neither compare nor <c>ORDER BY</c> one in SQL — so «this month» could only ever have been
/// applied in memory, over every download a workspace has ever served, on the panel's most-visited
/// page.</para>
/// </summary>
public class TrafficMeterTests
{
    [Fact]
    public async Task Two_downloads_on_one_day_land_on_one_row()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var meter = Meter(harness);

        await meter.RecordAsync(tenant.Id, 1_000, default);
        await meter.RecordAsync(tenant.Id, 2_500, default);

        var month = await meter.MonthAsync(tenant.Id, Today(harness), default);

        month.EgressBytes.Should().Be(3_500);
        month.Downloads.Should().Be(2);

        // One row and not two, which is the whole shape: the first call inserts and every one after
        // it is an UPDATE that adds. Thirty-one rows a month per workspace, not one per download.
        (await harness.Db.TenantUsageDays.CountAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_month_is_the_calendar_month_and_stops_at_its_edges()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var today = Today(harness);
        var first = new DateOnly(today.Year, today.Month, 1);

        // One row on the last day of last month, one on the first of this, one on the last of this.
        Seed(harness, tenant.Id, first.AddDays(-1), 500);
        Seed(harness, tenant.Id, first, 100);
        Seed(harness, tenant.Id, first.AddMonths(1).AddDays(-1), 200);
        Seed(harness, tenant.Id, first.AddMonths(1), 900);

        var month = await Meter(harness).MonthAsync(tenant.Id, today, default);

        // The two inside, and neither neighbour. A month that leaked a day either way would over-bill
        // a customer at exactly the moment they were looking at the number.
        month.EgressBytes.Should().Be(300);
    }

    [Fact]
    public async Task One_workspace_is_never_billed_for_anothers_bytes()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var meter = Meter(harness);

        await meter.RecordAsync(mine.Id, 1_000, default);
        await meter.RecordAsync(theirs.Id, 9_000, default);

        (await meter.MonthAsync(mine.Id, Today(harness), default)).EgressBytes.Should().Be(1_000);
        (await meter.MonthAsync(theirs.Id, Today(harness), default)).EgressBytes.Should().Be(9_000);
    }

    [Fact]
    public async Task A_workspace_that_has_served_nothing_is_zero_and_not_absent()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var month = await Meter(harness).MonthAsync(tenant.Id, Today(harness), default);

        // Zero rather than null, so the screens have a number to draw. The reason the dash was right
        // before this existed was that nothing counted — not that nothing had been served.
        month.Should().Be(UsageTotalNothing);
    }

    [Fact]
    public async Task A_range_returns_the_days_that_have_something_on_them()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var today = Today(harness);

        Seed(harness, tenant.Id, today.AddDays(-6), 10);
        Seed(harness, tenant.Id, today.AddDays(-3), 20);
        Seed(harness, tenant.Id, today, 30);

        var week = await Meter(harness).RangeAsync(tenant.Id, today.AddDays(-6), today, default);

        // Oldest first, and the quiet days absent rather than present and zero: a caller drawing a
        // chart fills the gaps, and a caller adding them up does not care.
        week.Select(d => d.EgressBytes).Should().Equal(10, 20, 30);
        week.Select(d => d.Day).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task The_operators_view_is_every_workspace_for_the_month()
    {
        await using var harness = ServiceTestHarness.Create();
        var one = harness.SeedTenant("acme");
        var two = harness.SeedTenant("globex");
        var today = Today(harness);

        Seed(harness, one.Id, today, 100);
        Seed(harness, one.Id, today.AddDays(-1), 50);
        Seed(harness, two.Id, today, 700);

        var all = await Meter(harness).EveryTenantMonthAsync(today, default);

        // The one method in this product with no tenant predicate, and it returns totals rather than
        // anything that could name a file. Its callers are behind the operator policy.
        all[one.Id].EgressBytes.Should().Be(150);
        all[two.Id].EgressBytes.Should().Be(700);
    }

    [Fact]
    public async Task A_negative_count_is_floored_rather_than_written()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        // A bug upstream, and the honest response is to record the download without corrupting the
        // month. Writing it would make a customer's usage go *down* as they served more.
        await Meter(harness).RecordAsync(tenant.Id, -5_000, default);

        var month = await Meter(harness).MonthAsync(tenant.Id, Today(harness), default);

        month.EgressBytes.Should().Be(0);
        month.Downloads.Should().Be(1);
    }

    private static readonly Core.Application.UsageTotal UsageTotalNothing = Core.Application.UsageTotal.Nothing;

    private static Infrastructure.Persistence.Repositories.TrafficMeter Meter(ServiceTestHarness harness) =>
        new(harness.Db, harness.Clock, NullLogger<Infrastructure.Persistence.Repositories.TrafficMeter>.Instance);

    private static DateOnly Today(ServiceTestHarness harness) =>
        DateOnly.FromDateTime(harness.Clock.GetUtcNow().UtcDateTime);

    private static void Seed(ServiceTestHarness harness, Guid tenantId, DateOnly day, long bytes)
    {
        harness.Db.TenantUsageDays.Add(new TenantUsageDay
        {
            TenantId = tenantId,
            Day = day,
            EgressBytes = bytes,
            Downloads = 1,
        });

        harness.Db.SaveChanges();
    }
}
