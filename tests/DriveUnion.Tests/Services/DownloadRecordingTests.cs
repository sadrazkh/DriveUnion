using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The counter the customer's download cap is spent from.
///
/// It is the one number in this product that two anonymous requests can move at the same moment,
/// which makes read-modify-write the obvious implementation and the wrong one.
/// </summary>
public class DownloadRecordingTests
{
    [Fact]
    public async Task One_counted_download_moves_the_counter_once_and_leaves_one_audit_row()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        await harness.PublicLinks().RecordDownloadAsync(link.Id, "ip-hash", "curl/8.5", default);

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.DownloadCount.Should().Be(1);

        var events = await harness.Db.DownloadEvents.AsNoTracking()
            .Where(d => d.ShareLinkId == link.Id).ToListAsync();

        events.Should().ContainSingle();
        events[0].IpHash.Should().Be("ip-hash");
        events[0].UserAgent.Should().Be("curl/8.5");
        events[0].OccurredAt.Should().Be(ServiceTestHarness.Now);
    }

    [Fact]
    public async Task Two_concurrent_records_produce_two_increments_not_one()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 500, downloadCount: 499);

        var contextA = harness.NewContext();
        var contextB = harness.NewContext();

        // Both callers read the row before either writes — the interleaving two simultaneous
        // downloads actually produce. Each now holds a snapshot saying 499. An implementation that
        // computed 499 + 1 in memory would write 500 twice and one download would vanish.
        var snapshotA = await contextA.ShareLinks.SingleAsync(l => l.Id == link.Id);
        var snapshotB = await contextB.ShareLinks.SingleAsync(l => l.Id == link.Id);
        snapshotA.DownloadCount.Should().Be(499);
        snapshotB.DownloadCount.Should().Be(499);

        await harness.PublicLinks(contextA).RecordDownloadAsync(link.Id, "ip-a", null, default);
        await harness.PublicLinks(contextB).RecordDownloadAsync(link.Id, "ip-b", null, default);

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.DownloadCount.Should().Be(501);

        var events = await harness.Db.DownloadEvents.AsNoTracking()
            .CountAsync(d => d.ShareLinkId == link.Id);
        events.Should().Be(2);
    }

    [Fact]
    public async Task The_counter_and_its_audit_row_move_together()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var reader = harness.PublicLinks();
        for (var i = 0; i < 5; i++)
        {
            await reader.RecordDownloadAsync(link.Id, $"ip-{i}", null, default);
        }

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        var events = await harness.Db.DownloadEvents.AsNoTracking()
            .CountAsync(d => d.ShareLinkId == link.Id);

        row.DownloadCount.Should().Be(5);
        events.Should().Be(5, "the denormalised counter and the audit trail behind it must agree");
    }

    [Fact]
    public async Task Recording_against_a_link_that_no_longer_exists_writes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();

        // The link vanished between the ticket and the last byte. There is no counter to move, and
        // an orphan audit row helps nobody.
        await harness.PublicLinks().RecordDownloadAsync(Guid.NewGuid(), "ip-hash", null, default);

        (await harness.Db.DownloadEvents.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Spending_the_last_download_closes_the_link()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 1);

        var reader = harness.PublicLinks();

        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().NotBeNull();
        await reader.RecordDownloadAsync(link.Id, "ip-hash", null, default);
        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
    }
}
