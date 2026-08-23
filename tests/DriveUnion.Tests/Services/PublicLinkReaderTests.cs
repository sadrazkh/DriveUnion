using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The anonymous half of the product. Nothing in this file mentions a tenant, and that is the
/// point: /d/{slug} arrives with no signed-in user, and a reader that wanted one would answer "not
/// found" for every live link in the product.
/// </summary>
public class PublicLinkReaderTests
{
    [Fact]
    public async Task A_live_link_resolves_with_no_tenant_involved_anywhere()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "quarterly.mp4", 4096);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);

        resolution.IsAvailable.Should().BeTrue();
        resolution.Reason.Should().Be(ShareLinkAvailability.Available);
        resolution.File.Should().NotBeNull();
        resolution.File!.FileName.Should().Be("quarterly.mp4");
        resolution.File.SizeBytes.Should().Be(4096);
    }

    [Fact]
    public async Task The_public_view_names_no_google_account_and_no_file_id()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);

        // The customer must never learn which account holds their file, and the Drive id must never
        // reach a response. PublicFileView carries neither, and this asserts the type stays that way.
        typeof(PublicFileView).GetProperties().Select(p => p.Name)
            .Should().NotContain(["GoogleAccountId", "DriveFileId", "TenantId"]);

        resolution.File.Should().NotBeNull();
    }

    [Fact]
    public async Task An_expired_link_is_unavailable_and_the_log_learns_why()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", expiresAt: ServiceTestHarness.Now.AddHours(1));

        var reader = harness.PublicLinks();

        (await reader.ResolveAsync("kx91mzq4", default)).IsAvailable.Should().BeTrue();

        harness.Clock.Advance(TimeSpan.FromHours(1));

        var resolution = await reader.ResolveAsync("kx91mzq4", default);

        resolution.IsAvailable.Should().BeFalse();
        resolution.Reason.Should().Be(ShareLinkAvailability.Expired);
        resolution.File.Should().BeNull("a refusing link that still named its file renders a "
                                        + "different card from an unknown slug");

        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_capped_link_is_unavailable_and_the_log_learns_why()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", maxDownloads: 500, downloadCount: 500);

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);

        resolution.IsAvailable.Should().BeFalse();
        resolution.Reason.Should().Be(ShareLinkAvailability.DownloadCapReached);
        resolution.File.Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_link_is_unavailable_and_the_log_learns_why()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", isActive: false);

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);

        resolution.IsAvailable.Should().BeFalse();
        resolution.Reason.Should().Be(ShareLinkAvailability.Revoked);
        resolution.File.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_slug_is_not_found_and_renders_exactly_like_a_refusal()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4", isActive: false);

        var reader = harness.PublicLinks();

        var unknown = await reader.ResolveAsync("zzzzzzzz", default);
        var revoked = await reader.ResolveAsync("kx91mzq4", default);
        var malformed = await reader.ResolveAsync("nope", default);

        unknown.Should().Be(PublicLinkResolution.NotFound);
        malformed.Should().Be(PublicLinkResolution.NotFound);

        // Everything the page can render is identical. Only Reason differs, and Reason is for the
        // owner's panel and the logs — telling "expired" apart from "never existed" is enough to
        // enumerate the slug space.
        unknown.IsAvailable.Should().Be(revoked.IsAvailable);
        unknown.File.Should().Be(revoked.File);

        (await reader.ResolveForDownloadAsync("zzzzzzzz", default)).Should().BeNull();
        (await reader.ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_download_ticket_carries_the_account_and_drive_id_the_streamer_needs()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var ticket = await harness.PublicLinks().ResolveForDownloadAsync("kx91mzq4", default);

        ticket.Should().NotBeNull();
        ticket!.ShareLinkId.Should().Be(link.Id);
        ticket.GoogleAccountId.Should().Be(account.Id);
        ticket.DriveFileId.Should().Be(file.DriveFileId);
    }
}
