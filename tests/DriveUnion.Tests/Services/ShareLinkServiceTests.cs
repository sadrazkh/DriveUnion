using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

public class ShareLinkServiceTests
{
    [Fact]
    public async Task A_new_link_starts_live_with_an_eight_character_slug()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var summary = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, ServiceTestHarness.Now.AddDays(7), 500),
            default);

        SlugGenerator.IsWellFormed(summary.Slug).Should().BeTrue();
        summary.IsActive.Should().BeTrue();
        summary.DownloadCount.Should().Be(0);
        summary.MaxDownloads.Should().Be(500);

        var resolution = await harness.PublicLinks().ResolveAsync(summary.Slug, default);
        resolution.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_slug_collision_is_retried_and_produces_a_working_link()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var other = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var taken = harness.SeedFile(other.Id, account.Id);

        // The slug space is shared across tenants, so the link already holding "kx91mzq4" belongs
        // to somebody else entirely — which is exactly how a real collision would arrive.
        harness.SeedLink(other.Id, taken.Id, "kx91mzq4");

        var slugs = new ScriptedSlugGenerator("kx91mzq4", "ab12cd34");

        var summary = await harness.Links(slugs).CreateAsync(
            tenant.Id, new CreateShareLinkRequest(file.Id, null, null), default);

        slugs.CallCount.Should().Be(2, "the first draw collided and had to be redrawn");
        summary.Slug.Should().Be("ab12cd34");

        // The retry is only worth anything if what it produced actually resolves.
        var resolution = await harness.PublicLinks().ResolveAsync("ab12cd34", default);
        resolution.IsAvailable.Should().BeTrue();

        // And the incumbent is untouched — a retry must not have overwritten somebody's live link.
        var incumbent = await harness.PublicLinks().ResolveForDownloadAsync("kx91mzq4", default);
        incumbent.Should().NotBeNull();
        incumbent!.DriveFileId.Should().Be(taken.DriveFileId);
    }

    [Fact]
    public async Task A_collision_on_every_draw_gives_up_rather_than_looping()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        // A generator that always returns the same slug is broken, and the bounded retry has to say
        // so rather than spin until the request times out.
        var slugs = new ScriptedSlugGenerator(
            "kx91mzq4", "kx91mzq4", "kx91mzq4", "kx91mzq4", "kx91mzq4", "kx91mzq4");

        var act = () => harness.Links(slugs).CreateAsync(
            tenant.Id, new CreateShareLinkRequest(file.Id, null, null), default);

        await act.Should().ThrowAsync<Exception>();
        slugs.CallCount.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task Links_are_listed_newest_first_and_only_for_their_own_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var otherFile = harness.SeedFile(tenant.Id, account.Id, "other.zip");

        harness.SeedLink(tenant.Id, file.Id, "aaaaaaaa");
        harness.SeedLink(tenant.Id, otherFile.Id, "bbbbbbbb");

        var links = await harness.Links().ListForFileAsync(tenant.Id, file.Id, default);

        links.Should().ContainSingle();
        links[0].Slug.Should().Be("aaaaaaaa");
    }

    [Fact]
    public async Task Revoking_twice_reports_the_second_attempt_as_a_no_op()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var links = harness.Links();

        (await links.RevokeAsync(tenant.Id, link.Id, default)).Should().BeTrue();
        (await links.RevokeAsync(tenant.Id, link.Id, default)).Should().BeFalse();

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);
        resolution.Reason.Should().Be(ShareLinkAvailability.Revoked);
    }

    [Fact]
    public async Task The_file_listing_counts_live_links_only()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "aaaaaaaa");
        harness.SeedLink(tenant.Id, file.Id, "bbbbbbbb", isActive: false);

        var listing = await harness.Files().ListAsync(tenant.Id, folderId: null, nameQuery: null, default);

        listing.Should().ContainSingle();
        listing[0].ActiveLinkCount.Should().Be(1);

        var detail = await harness.Files().GetAsync(tenant.Id, file.Id, default);
        detail!.Links.Should().HaveCount(2, "the panel shows revoked links too, it just does not count them");
    }
}
