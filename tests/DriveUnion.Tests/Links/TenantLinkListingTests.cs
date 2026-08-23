using DriveUnion.Tests.Services;
using FluentAssertions;

namespace DriveUnion.Tests.Links;

/// <summary>
/// <c>IShareLinkService.ListForTenantAsync</c> — the query «لینک‌های اشتراک» was missing.
///
/// It is the first tenant-wide read in the panel, so it is also the first place the isolation rule
/// of §8 could be got wrong at a new angle: every other listing narrows to one file first, and a
/// forgotten <c>tenantId</c> there is masked by the file id. Here the tenant argument is the only
/// thing between one customer's table and everybody's.
/// </summary>
public class TenantLinkListingTests
{
    [Fact]
    public async Task Every_link_the_tenant_owns_comes_back_with_the_file_it_points_at()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var report = harness.SeedFile(tenant.Id, account.Id, "Q3-Report-Final.pdf");
        var reel = harness.SeedFile(tenant.Id, account.Id, "promo-reel-4k.mp4");

        harness.SeedLink(tenant.Id, report.Id, "kx91mzq4", maxDownloads: 500, downloadCount: 241);
        harness.SeedLink(tenant.Id, reel.Id, "8vaq2cq1");

        var rows = await harness.Links().ListForTenantAsync(tenant.Id, default);

        rows.Should().HaveCount(2);

        // The file name is the table's first column and ShareLinkSummary cannot carry it, which is
        // the whole reason this method returns more than a summary.
        rows.Select(r => r.FileName).Should().BeEquivalentTo("Q3-Report-Final.pdf", "promo-reel-4k.mp4");
        rows.Select(r => r.StoredFileId).Should().BeEquivalentTo(new[] { report.Id, reel.Id });

        var capped = rows.Single(r => r.Link.Slug == "kx91mzq4");
        capped.Link.DownloadCount.Should().Be(241);
        capped.Link.MaxDownloads.Should().Be(500);
    }

    [Fact]
    public async Task Tenant_B_sees_none_of_tenant_As_links()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(a.Id, account.Id, "acme-payroll.zip");

        harness.SeedLink(a.Id, file.Id, "kx91mzq4");

        var links = harness.Links();

        (await links.ListForTenantAsync(a.Id, default)).Should().ContainSingle();
        (await links.ListForTenantAsync(b.Id, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revoked_links_are_listed_too_because_the_panel_is_where_they_are_explained()
    {
        // The public side collapses revoked, expired and never-existed into one card so a visitor
        // cannot tell them apart. The owner's table is the place that distinction was kept for, so
        // a revoked link has to appear in it rather than vanish.
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        harness.SeedLink(tenant.Id, file.Id, "we12nnq7", isActive: false);

        var rows = await harness.Links().ListForTenantAsync(tenant.Id, default);

        rows.Should().ContainSingle().Which.Link.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_link_over_a_deleted_file_drops_out_rather_than_naming_a_file_that_is_gone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        (await harness.Files().DeleteAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        // Deleting a file revokes its links, so what would be listed is a dead row naming something
        // the tenant already removed.
        (await harness.Links().ListForTenantAsync(tenant.Id, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Links_are_listed_newest_first()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var older = harness.SeedLink(tenant.Id, file.Id, "aaaaaaaa");
        harness.SeedLink(tenant.Id, file.Id, "bbbbbbbb");

        // The harness stamps every row with the same instant, so one of them has to be aged by hand
        // for the ordering to be observable at all.
        older.CreatedAt = ServiceTestHarness.Now.AddDays(-1);
        harness.Db.SaveChanges();

        var rows = await harness.Links().ListForTenantAsync(tenant.Id, default);

        rows.Select(r => r.Link.Slug).Should().ContainInOrder("bbbbbbbb", "aaaaaaaa");
    }
}
