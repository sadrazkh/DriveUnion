using System.Net;
using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Tests.Identity;
using DriveUnion.Web.Controllers;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Tests.Links;

/// <summary>
/// «لینک‌های اشتراک» — the page the shell has always pointed at.
///
/// The defect was not a broken page but an absent one: the sidebar's own comment says a nav item
/// that 404s teaches an operator to distrust the whole menu, and this item did. So the first test
/// here asks the real pipeline for the address in the markup.
/// </summary>
public class LinksPageTests
{
    private static readonly Guid TenantId = Guid.Parse("2b0b0d3a-4b4e-4a1f-9d0b-0c1a2f3e4d55");

    private static readonly Guid ReportId = Guid.Parse("8f1c6d21-2a55-4f0e-a3b7-6d9c0e1f2a34");

    [Fact]
    public async Task The_address_the_shell_points_at_is_answered_rather_than_404d()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        // Anonymous, so the honest answer is the sign-in challenge. What it must not be — and was —
        // is a 404 for a link printed in the sidebar of every page in the panel.
        using var response = await client.GetAsync(new Uri("/Links", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString
            .Should().Contain("/Identity/Account/Login")
            .And.Contain(
                "ReturnUrl=%2FLinks",
                "the challenge only carries a return URL for an address that routed somewhere");
    }

    [Fact]
    public async Task The_table_draws_the_comps_five_columns_from_the_tenants_own_links()
    {
        var links = new StubShareLinkService(
        [
            (Summary("kx91mzq4", maxDownloads: 500, downloadCount: 241), ReportId, "Q3-Report-Final.pdf"),
        ]);

        var model = await RenderAsync(links);

        links.AskedFor.Should().Be(TenantId, "the tenant is an argument, never an ambient filter");

        var row = model.Rows.Should().ContainSingle().Subject;
        row.FileName.Should().Be("Q3-Report-Final.pdf");
        row.SlugPath.Should().Be("/d/kx91mzq4");
        row.DownloadsText.Should().Be("۲۴۱/۵۰۰");
        row.ExpiryText.Should().Be("بدون");
        row.Status.Should().Be(LinkStatus.Active);

        // The row is a way in to the file, which is where copy and revoke already live.
        row.FileId.Should().Be(ReportId);
    }

    [Fact]
    public async Task A_link_with_no_cap_reads_as_unlimited_rather_than_as_a_number()
    {
        var links = new StubShareLinkService(
        [
            (Summary("8vaq2cq1", downloadCount: 189), ReportId, "promo-reel-4k.mp4"),
        ]);

        var model = await RenderAsync(links);

        model.Rows.Single().DownloadsText.Should().Be("۱۸۹/∞");
    }

    [Fact]
    public void Nothing_the_page_renders_names_a_google_account()
    {
        // The product model in one assertion: a customer must never learn that their file sits in
        // somebody's Google Drive, so the row type has nowhere to put an account even by accident.
        typeof(LinkRowViewModel).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name =>
                name.Contains("Account", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Google", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Drive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_caller_with_no_tenant_is_refused_rather_than_scoped_to_nobody()
    {
        // The failure §8 was written about: a request with no usable tenant claim must be turned
        // away, not handed Guid.Empty and shown an empty table that looks like "you have no links".
        var links = new StubShareLinkService([]);
        var controller = Build(links, principal: new ClaimsPrincipal(new ClaimsIdentity()));

        var result = await controller.Index(CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        links.AskedFor.Should().BeNull("nothing may be read on behalf of a caller with no tenant");
    }

    [Theory]
    [InlineData(0, null, LinkStatus.Active)]
    [InlineData(241, 500, LinkStatus.Active)]
    [InlineData(76, 100, LinkStatus.NearCap)]
    [InlineData(5, 5, LinkStatus.CapReached)]
    [InlineData(9, 5, LinkStatus.CapReached)]
    public void The_status_column_reads_the_comps_own_rows_the_way_the_comp_draws_them(
        int downloadCount,
        int? maxDownloads,
        LinkStatus expected)
    {
        // ۲۴۱/۵۰۰ is drawn «فعال» and ۷۶/۱۰۰ is drawn «نزدیک سقف» in the handoff, which is what
        // fixes the threshold between them.
        var link = Summary("kx91mzq4", maxDownloads, downloadCount);

        LinkStatuses.Classify(link, Now).Should().Be(expected);
    }

    [Fact]
    public void A_revoked_link_reads_as_revoked_even_when_it_has_downloads_left()
    {
        var link = Summary("we12nnq7", maxDownloads: 500, downloadCount: 5, isActive: false);

        LinkStatuses.Classify(link, Now).Should().Be(LinkStatus.Revoked);
    }

    [Fact]
    public void An_expired_link_reads_as_expired_rather_than_as_active()
    {
        var link = Summary("rt40abq2", expiresAt: Now.AddMinutes(-1));

        LinkStatuses.Classify(link, Now).Should().Be(LinkStatus.Expired);
    }

    private static DateTimeOffset Now => new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static ShareLinkSummary Summary(
        string slug,
        int? maxDownloads = null,
        int downloadCount = 0,
        bool isActive = true,
        DateTimeOffset? expiresAt = null) =>
        new(Guid.NewGuid(), slug, expiresAt, maxDownloads, downloadCount, isActive);

    private static async Task<LinksPageViewModel> RenderAsync(IShareLinkService links)
    {
        var result = await Build(links).Index(CancellationToken.None);

        return result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<LinksPageViewModel>().Subject;
    }

    private static LinksController Build(IShareLinkService links, ClaimsPrincipal? principal = null)
    {
        var context = new DefaultHttpContext
        {
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(DriveUnionClaimTypes.TenantId, TenantId.ToString())],
                authenticationType: "Test")),
        };

        return new LinksController(links)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    /// <summary>The one method the page uses. The other three have no business being reachable here.</summary>
    private sealed class StubShareLinkService(
        IReadOnlyList<(ShareLinkSummary Link, Guid StoredFileId, string FileName)> rows) : IShareLinkService
    {
        public Guid? AskedFor { get; private set; }

        public Task<IReadOnlyList<(ShareLinkSummary Link, Guid StoredFileId, string FileName)>>
            ListForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            AskedFor = tenantId;

            return Task.FromResult(rows);
        }

        public Task<ShareLinkSummary> CreateAsync(
            Guid tenantId,
            CreateShareLinkRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ShareLinkSummary>> ListForFileAsync(
            Guid tenantId,
            Guid fileId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
