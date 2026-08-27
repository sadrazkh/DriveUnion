using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Dashboard;
using DriveUnion.Tests.Services;
using FluentAssertions;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// The operator's two questions, answered over a real relational database: is storage running out,
/// and is anything broken.
/// </summary>
public class OperatorDashboardReaderTests
{
    private static readonly DateTimeOffset Now = ServiceTestHarness.Now;

    private const long OneTerabyte = 1024L * 1024 * 1024 * 1024;

    /// <summary>
    /// A disconnected account is not capacity — nothing can be written to it — so it is out of both
    /// pool totals. Counting it would make the pool look larger than it is on exactly the day the
    /// operator most needs the figure to be right.
    /// </summary>
    [Fact]
    public async Task A_disconnected_account_is_listed_and_is_not_capacity()
    {
        await using var harness = ServiceTestHarness.Create();

        harness.SeedAccount(quotaTotalBytes: 5 * OneTerabyte, quotaUsedBytes: OneTerabyte);
        harness.SeedAccount(
            GoogleAccountStatus.Disconnected,
            quotaTotalBytes: 5 * OneTerabyte,
            quotaUsedBytes: 2 * OneTerabyte);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.Accounts.Should().HaveCount(2, "the operator still has to see the broken one");
        dashboard.DisconnectedAccountCount.Should().Be(1);

        dashboard.PoolTotalBytes.Should().Be(5 * OneTerabyte, "only what can actually be written to");
        dashboard.PoolUsedBytes.Should().Be(OneTerabyte);
    }

    [Fact]
    public async Task Only_the_workspaces_past_the_threshold_are_listed_and_the_fullest_is_first()
    {
        await using var harness = ServiceTestHarness.Create();

        var comfortable = harness.SeedTenant("comfortable");
        var tight = harness.SeedTenant("tight");
        var full = harness.SeedTenant("full");

        Fill(harness, comfortable.Id, used: 10, cap: 100);
        Fill(harness, tight.Id, used: 85, cap: 100);
        Fill(harness, full.Id, used: 100, cap: 100);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.WorkspaceCount.Should().Be(3);
        dashboard.NearCeilingPercent.Should().Be(80);

        dashboard.WorkspacesNearTheirCeiling
            .Select(w => w.Name)
            .Should().Equal("full", "tight");

        dashboard.CommittedStorageBytes.Should().Be(300, "the sum of every cap, comfortable or not");
    }

    /// <summary>
    /// A cap of zero reads as full rather than as a division by zero — the same rule the customer's
    /// own plan card applies, so a workspace on this list and that customer's screen agree.
    /// </summary>
    [Fact]
    public async Task A_workspace_with_no_cap_at_all_counts_as_full()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("capless");
        Fill(harness, tenant.Id, used: 0, cap: 0);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.WorkspacesNearTheirCeiling.Should().ContainSingle()
            .Which.Name.Should().Be("capless");
    }

    /// <summary>
    /// In flight means Google would still take a chunk. An expired session's resumable URI is dead
    /// and the browser has to start over, so counting it would leave a permanent number on a card
    /// whose whole job is to read zero when nothing is happening.
    /// </summary>
    [Fact]
    public async Task An_expired_session_is_not_in_flight()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedSession(
            tenant.Id, account.Id, "alive.zst", UploadSessionStatus.InProgress, Now, Now.AddDays(6));

        harness.SeedSession(
            tenant.Id, account.Id, "stale.zst", UploadSessionStatus.InProgress, Now.AddDays(-8), Now.AddDays(-1));

        harness.SeedSession(
            tenant.Id, account.Id, "done.zst", UploadSessionStatus.Completed, Now, Now.AddDays(6));

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.TransfersInFlight.Should().Be(1);
    }

    [Fact]
    public async Task Failures_are_counted_in_the_window_and_the_list_is_bounded_by_it()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedSession(
            tenant.Id,
            account.Id,
            "dataset-full.img",
            UploadSessionStatus.Failed,
            Now.AddHours(-1),
            Now.AddDays(6),
            "403 userRateLimitExceeded");

        harness.SeedSession(
            tenant.Id,
            account.Id,
            "last-week.img",
            UploadSessionStatus.Failed,
            Now.AddHours(-OperatorDashboardReader.FailureWindowHours - 1),
            Now.AddDays(6),
            "an older failure");

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.FailureWindowHours.Should().Be(OperatorDashboardReader.FailureWindowHours);
        dashboard.TransfersFailedInWindow.Should().Be(1, "a failure from last week is history");

        dashboard.RecentFailures.Should().ContainSingle();
        dashboard.RecentFailures[0].FileName.Should().Be("dataset-full.img");
        dashboard.RecentFailures[0].Reason.Should().Be(
            "403 userRateLimitExceeded",
            "the words the failure arrived in, untranslated — it is what an operator searches for");
    }

    [Fact]
    public async Task An_empty_panel_reads_as_empty_rather_than_as_a_pool_of_zeroes()
    {
        await using var harness = ServiceTestHarness.Create();

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.Accounts.Should().BeEmpty();
        dashboard.PoolTotalBytes.Should().Be(0);
        dashboard.WorkspaceCount.Should().Be(0);
        dashboard.WorkspacesNearTheirCeiling.Should().BeEmpty();
        dashboard.TransfersInFlight.Should().Be(0);
        dashboard.RecentFailures.Should().BeEmpty();

        dashboard.IsOverCommitted.Should().BeFalse(
            "nothing has been sold and nothing has been connected, so nothing is over-committed");
    }

    /// <summary>Over-commitment is displayed rather than prevented: a cap is a ceiling, not a reservation.</summary>
    [Fact]
    public async Task Selling_more_than_the_pool_holds_is_reported_and_not_refused()
    {
        await using var harness = ServiceTestHarness.Create();

        harness.SeedAccount(quotaTotalBytes: OneTerabyte);

        var tenant = harness.SeedTenant("greedy");
        Fill(harness, tenant.Id, used: 0, cap: 2 * OneTerabyte);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.CommittedStorageBytes.Should().Be(2 * OneTerabyte);
        dashboard.PoolTotalBytes.Should().Be(OneTerabyte);
        dashboard.IsOverCommitted.Should().BeTrue();
    }

    /// <summary>
    /// <b>The egress chart's window: one entry per day, quiet days included.</b>
    ///
    /// <para><c>ITrafficMeter</c> returns only the days that have something on them, which is right
    /// for a caller adding them up and catastrophic for one drawing them — a chart handed a sparse
    /// list puts Monday's column where Sunday's belongs and silently re-labels every column after it.
    /// That is the one way a chart can be wrong that a reader cannot see, so the filling happens here
    /// where a test can reach it rather than in a Razor loop where it cannot.</para>
    /// </summary>
    [Fact]
    public async Task The_egress_window_has_one_entry_per_day_including_the_quiet_ones()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Served(harness, tenant.Id, today, 400);
        Served(harness, tenant.Id, today.AddDays(-3), 100);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.EgressWindowDays.Should().Be(OperatorDashboardReader.EgressWindowDays);
        dashboard.EgressByDay.Should().HaveCount(OperatorDashboardReader.EgressWindowDays);

        // Oldest first, ending on today: the window is thirty days counting today, so the last entry
        // is today and the first is twenty-nine days before it.
        dashboard.EgressByDay[^1].Day.Should().Be(today);
        dashboard.EgressByDay[0].Day.Should()
            .Be(today.AddDays(-(OperatorDashboardReader.EgressWindowDays - 1)));

        dashboard.EgressByDay.Select(d => d.Day).Should().BeInAscendingOrder();

        // The two days that had traffic, in their own places, and the rest present and zero.
        dashboard.EgressByDay[^1].EgressBytes.Should().Be(400);
        dashboard.EgressByDay[^4].EgressBytes.Should().Be(100);
        dashboard.EgressByDay.Count(d => d.EgressBytes == 0).Should()
            .Be(OperatorDashboardReader.EgressWindowDays - 2);
    }

    /// <summary>
    /// The two figures the caption is drawn from: the window's total, and the day the columns are
    /// scaled against.
    ///
    /// <para>They are separate on purpose. There is no ceiling to draw an egress column against —
    /// what a plan sells is per workspace and this is every workspace, and nothing has measured what
    /// the box's uplink can do — so the tallest day is the scale, and a peak that was quietly the
    /// total would flatten every chart in the product into one full column and twenty-nine empty ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_window_carries_its_total_and_its_busiest_day_apart()
    {
        await using var harness = ServiceTestHarness.Create();
        var one = harness.SeedTenant("acme");
        var two = harness.SeedTenant("globex");
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Served(harness, one.Id, today, 300);
        Served(harness, two.Id, today, 200);
        Served(harness, one.Id, today.AddDays(-1), 900);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.EgressWindowBytes.Should().Be(1_400, "every workspace on every day of the window");
        dashboard.EgressPeakDayBytes.Should().Be(900, "yesterday, and not the total");
    }

    /// <summary>
    /// A product that has served nothing has a peak of zero, which is what the screen checks before
    /// it divides by it. An empty window is the ordinary state of a fresh deployment, not an edge.
    /// </summary>
    [Fact]
    public async Task A_window_with_nothing_in_it_has_a_peak_of_zero_rather_than_no_days()
    {
        await using var harness = ServiceTestHarness.Create();
        harness.SeedTenant("acme");

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.EgressByDay.Should().HaveCount(OperatorDashboardReader.EgressWindowDays);
        dashboard.EgressWindowBytes.Should().Be(0);
        dashboard.EgressPeakDayBytes.Should().Be(0);
    }

    /// <summary>
    /// Traffic older than the window is out of it, however much of it there was.
    ///
    /// <para>The rolling window is the whole reason this is not a calendar month: an operator's
    /// question is «what is happening now», and a spike from six weeks ago answering it would be the
    /// dashboard reporting history as news.</para>
    /// </summary>
    [Fact]
    public async Task Traffic_older_than_the_window_is_not_in_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Served(harness, tenant.Id, today.AddDays(-OperatorDashboardReader.EgressWindowDays), 9_000);
        Served(harness, tenant.Id, today, 7);

        var dashboard = await harness.OperatorDashboard().ReadAsync(default);

        dashboard.EgressWindowBytes.Should().Be(7);
    }

    private static void Fill(ServiceTestHarness harness, Guid tenantId, long used, long cap)
    {
        var tenant = harness.Db.Tenants.Single(t => t.Id == tenantId);

        tenant.StorageUsedBytes = used;
        tenant.StorageQuotaBytes = cap;

        harness.Db.SaveChanges();
    }

    /// <summary>
    /// One day's traffic for one workspace, as the roll-up row the meter would have written.
    ///
    /// <para>Written straight to the table: what is under test is the window and the filling, and
    /// going through <c>RecordAsync</c> would date every row today and leave nothing to draw.</para>
    /// </summary>
    private static void Served(ServiceTestHarness harness, Guid tenantId, DateOnly day, long bytes)
    {
        harness.Db.TenantUsageDays.Add(new Core.Metering.TenantUsageDay
        {
            TenantId = tenantId,
            Day = day,
            EgressBytes = bytes,
            Downloads = 1,
        });

        harness.Db.SaveChanges();
    }
}
