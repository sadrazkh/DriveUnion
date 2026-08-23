using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The counter the customer's download cap is spent from, and the reserve → record-or-release cycle
/// that spends it.
///
/// It is the one number in this product that two anonymous requests can move at the same moment,
/// which makes read-modify-write the obvious implementation and the wrong one. The slot is taken
/// before Google is contacted and given back if the download never happens, because the alternative
/// — take it when the last byte lands — leaves the cap unenforced for the length of a transfer.
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

        var reader = harness.PublicLinks();

        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
        await reader.RecordDownloadAsync(link.Id, "ip-hash", "curl/8.5", default);

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.DownloadCount.Should().Be(1, "the reservation moved it, and recording must not move it again");

        var events = await harness.Db.DownloadEvents.AsNoTracking()
            .Where(d => d.ShareLinkId == link.Id).ToListAsync();

        events.Should().ContainSingle();
        events[0].IpHash.Should().Be("ip-hash");
        events[0].UserAgent.Should().Be("curl/8.5");
        events[0].OccurredAt.Should().Be(ServiceTestHarness.Now);
    }

    [Fact]
    public async Task The_counter_moves_when_the_slot_is_taken_and_not_when_the_bytes_land()
    {
        // Which is the whole fix: while a 214 GB transfer is in flight its slot is already spent, so
        // the request behind it is measured against a count that includes the one still running.
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var reader = harness.PublicLinks();

        await reader.TryReserveDownloadAsync(link.Id, default);

        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .DownloadCount.Should().Be(1);
        (await harness.Db.DownloadEvents.AsNoTracking().CountAsync()).Should().Be(0,
            "nothing has been delivered yet, so there is nothing to put in the audit trail");

        await reader.RecordDownloadAsync(link.Id, "ip-hash", null, default);

        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .DownloadCount.Should().Be(1);
        (await harness.Db.DownloadEvents.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_reservations_that_both_read_499_produce_two_increments_not_one()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", downloadCount: 499);

        var contextA = harness.NewContext();
        var contextB = harness.NewContext();

        // Both callers read the row before either writes — the interleaving two simultaneous
        // downloads actually produce. Each now holds a snapshot saying 499. An implementation that
        // computed 499 + 1 in memory would write 500 twice and one download would vanish.
        var snapshotA = await contextA.ShareLinks.SingleAsync(l => l.Id == link.Id);
        var snapshotB = await contextB.ShareLinks.SingleAsync(l => l.Id == link.Id);
        snapshotA.DownloadCount.Should().Be(499);
        snapshotB.DownloadCount.Should().Be(499);

        (await harness.PublicLinks(contextA).TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
        (await harness.PublicLinks(contextB).TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.DownloadCount.Should().Be(501, "the link has no cap, so both requests were entitled to a slot");
    }

    [Fact]
    public async Task Two_reservations_for_the_last_slot_are_granted_to_exactly_one_of_them()
    {
        // 499 of 500, and two visitors. The database decides, in the same statement that moves the
        // number: a check followed by a write hands the same slot to both.
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 500, downloadCount: 499);

        var contextA = harness.NewContext();
        var contextB = harness.NewContext();

        var snapshotA = await contextA.ShareLinks.SingleAsync(l => l.Id == link.Id);
        var snapshotB = await contextB.ShareLinks.SingleAsync(l => l.Id == link.Id);
        snapshotA.DownloadCount.Should().Be(499);
        snapshotB.DownloadCount.Should().Be(499);

        var first = await harness.PublicLinks(contextA).TryReserveDownloadAsync(link.Id, default);
        var second = await harness.PublicLinks(contextB).TryReserveDownloadAsync(link.Id, default);

        first.Should().BeTrue();
        second.Should().BeFalse("the cap is 500 and the first request took the five hundredth");

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.DownloadCount.Should().Be(500, "a refused reservation must not move the counter at all");
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
            (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
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

        // The link vanished between the reservation and the last byte. DownloadEvent has a foreign
        // key to it, and an orphan audit row helps nobody even where one could be written.
        await harness.PublicLinks().RecordDownloadAsync(Guid.NewGuid(), "ip-hash", null, default);

        (await harness.Db.DownloadEvents.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_link_that_is_gone_or_revoked_grants_no_reservation()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var revoked = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", isActive: false);

        var reader = harness.PublicLinks();

        (await reader.TryReserveDownloadAsync(revoked.Id, default)).Should().BeFalse();
        (await reader.TryReserveDownloadAsync(Guid.NewGuid(), default)).Should().BeFalse();

        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == revoked.Id);
        row.DownloadCount.Should().Be(0, "an owner revoking a link mid-request stops the next one");
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

        // The reservation alone closes it, before a single byte has been delivered.
        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();

        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_released_slot_is_spendable_again()
    {
        // A cap of one, and a transfer that never happened. The visitor behind it gets the download
        // the first one did not take.
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 1);

        var reader = harness.PublicLinks();

        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
        await reader.ReleaseDownloadAsync(link.Id, default);

        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .DownloadCount.Should().Be(0);
        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().NotBeNull();
        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();

        (await harness.Db.DownloadEvents.AsNoTracking().CountAsync()).Should().Be(0,
            "a released reservation was never a download and has no place in the audit trail");
    }

    [Fact]
    public async Task Releasing_more_than_was_reserved_cannot_drive_the_counter_below_zero()
    {
        // A negative counter is not a cosmetic problem: MaxDownloads is enforced against this number,
        // so -3 is three free downloads for whoever finds the link.
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 1);

        var reader = harness.PublicLinks();

        await reader.ReleaseDownloadAsync(link.Id, default);

        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
        await reader.ReleaseDownloadAsync(link.Id, default);
        await reader.ReleaseDownloadAsync(link.Id, default);

        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .DownloadCount.Should().Be(0);

        // And the cap still means one, rather than one plus however far the counter went under.
        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeTrue();
        (await reader.TryReserveDownloadAsync(link.Id, default)).Should().BeFalse();
    }
}
