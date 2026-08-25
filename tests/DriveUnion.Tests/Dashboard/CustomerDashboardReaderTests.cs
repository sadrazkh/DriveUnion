using DriveUnion.Tests.Services;
using FluentAssertions;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// The customer's figures, counted over a real relational database.
///
/// <para>Every assertion here is about a number being <b>true</b> rather than present. A dashboard
/// of plausible figures is worse than the redirect it replaced: it is read, believed, and acted on
/// — a customer who is told they have four live links when two expired last week stops checking.
/// </para>
/// </summary>
public class CustomerDashboardReaderTests
{
    private static readonly DateTimeOffset Now = ServiceTestHarness.Now;

    [Fact]
    public async Task A_workspace_that_does_not_exist_reads_as_nothing_rather_than_as_zeroes()
    {
        await using var harness = ServiceTestHarness.Create();

        var dashboard = await harness.CustomerDashboard().ReadAsync(Guid.NewGuid(), default);

        dashboard.Should().BeNull(
            "a screen of zeroes for a workspace that does not exist is how a broken session comes to "
            + "read as a customer with an empty account");
    }

    /// <summary>
    /// «Live» is <c>ShareLink.Evaluate</c> itself — the very method <c>/d/{slug}</c> refuses on — so
    /// the owner's dashboard and the public page cannot come to disagree about which links still
    /// work. A revoked link, an expired one and one that has spent its cap are all links the public
    /// page turns away, so none of them is live here either.
    /// </summary>
    [Fact]
    public async Task Only_the_links_that_still_serve_are_counted_live()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        harness.SeedLink(tenant.Id, file.Id, "live-none");
        harness.SeedLink(tenant.Id, file.Id, "live-room", maxDownloads: 10, downloadCount: 9);
        harness.SeedLink(tenant.Id, file.Id, "live-later", expiresAt: Now.AddDays(1));

        harness.SeedLink(tenant.Id, file.Id, "dead-revoked", isActive: false);
        harness.SeedLink(tenant.Id, file.Id, "dead-expired", expiresAt: Now.AddDays(-1));
        harness.SeedLink(tenant.Id, file.Id, "dead-capped", maxDownloads: 10, downloadCount: 10);

        var dashboard = await harness.CustomerDashboard().ReadAsync(tenant.Id, default);

        dashboard.Should().NotBeNull();
        dashboard!.LinkCount.Should().Be(6);
        dashboard.LiveLinkCount.Should().Be(3);
    }

    [Fact]
    public async Task Another_workspaces_links_and_downloads_are_not_counted()
    {
        await using var harness = ServiceTestHarness.Create();

        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var myFile = harness.SeedFile(mine.Id, account.Id, "mine.pdf");
        var theirFile = harness.SeedFile(theirs.Id, account.Id, "theirs.pdf");

        var myLink = harness.SeedLink(mine.Id, myFile.Id, "mine-1", downloadCount: 2);
        var theirLink = harness.SeedLink(theirs.Id, theirFile.Id, "theirs-1", downloadCount: 40);

        var dashboard = await harness.CustomerDashboard().ReadAsync(mine.Id, default);

        dashboard.Should().NotBeNull();
        dashboard!.LinkCount.Should().Be(1);
        dashboard.DownloadsAllTime.Should().Be(2, "forty of them are somebody else's");
        dashboard.RecentUploads.Should().ContainSingle().Which.Name.Should().Be("mine.pdf");
        dashboard.BusiestLinks.Should().ContainSingle().Which.Slug.Should().Be("mine-1");
    }

    [Fact]
    public async Task The_newest_uploads_come_first_and_a_deleted_file_is_not_one_of_them()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "older.pdf");
        harness.SeedFile(tenant.Id, account.Id, "deleted.pdf", deletedAt: Now);

        // The harness stamps every file with its own fixed Now, so the newest is made newest by
        // moving the row rather than by moving the clock.
        var newest = harness.SeedFile(tenant.Id, account.Id, "newest.pdf");
        newest.CreatedAt = Now.AddMinutes(5);
        harness.Db.SaveChanges();

        var dashboard = await harness.CustomerDashboard().ReadAsync(tenant.Id, default);

        dashboard.Should().NotBeNull();
        dashboard!.RecentUploads.Select(u => u.Name).Should().Equal("newest.pdf", "older.pdf");
    }

    /// <summary>
    /// The trash's size comes from <c>ITrash</c> — the same service the sidebar's capacity card
    /// reads — so the figure on the dashboard and the figure above the customer's name cannot come
    /// to disagree about what a delete did not free.
    /// </summary>
    [Fact]
    public async Task The_trash_figure_is_the_one_the_capacity_card_shows()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "live.pdf", sizeBytes: 4_096);
        harness.SeedFile(tenant.Id, account.Id, "gone.pdf", sizeBytes: 2_048, deletedAt: Now);
        harness.SeedFile(tenant.Id, account.Id, "also-gone.pdf", sizeBytes: 1_024, deletedAt: Now);

        var dashboard = await harness.CustomerDashboard().ReadAsync(tenant.Id, default);

        dashboard.Should().NotBeNull();
        dashboard!.TrashBytes.Should().Be(3_072);
        dashboard.TrashFileCount.Should().Be(2);
        dashboard.Plan.FileCount.Should().Be(1, "a file in the trash is not a file the customer has");
    }

    /// <summary>
    /// A link nobody has opened is not «busiest», it is «nobody has opened it». A list padded with
    /// those says less than a shorter one.
    /// </summary>
    [Fact]
    public async Task The_busiest_links_are_the_ones_that_have_actually_served_something()
    {
        await using var harness = ServiceTestHarness.Create();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "Q3-Report-Final.pdf");

        harness.SeedLink(tenant.Id, file.Id, "quiet");
        harness.SeedLink(tenant.Id, file.Id, "busy", downloadCount: 241, maxDownloads: 500);
        harness.SeedLink(tenant.Id, file.Id, "busier", downloadCount: 900);

        var dashboard = await harness.CustomerDashboard().ReadAsync(tenant.Id, default);

        dashboard.Should().NotBeNull();
        dashboard!.BusiestLinks.Select(l => l.Slug).Should().Equal("busier", "busy");
        dashboard.BusiestLinks[0].FileName.Should().Be("Q3-Report-Final.pdf");
        dashboard.BusiestLinks[1].MaxDownloads.Should().Be(500);
    }
}
