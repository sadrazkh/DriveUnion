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

    private static void Fill(ServiceTestHarness harness, Guid tenantId, long used, long cap)
    {
        var tenant = harness.Db.Tenants.Single(t => t.Id == tenantId);

        tenant.StorageUsedBytes = used;
        tenant.StorageQuotaBytes = cap;

        harness.Db.SaveChanges();
    }
}
