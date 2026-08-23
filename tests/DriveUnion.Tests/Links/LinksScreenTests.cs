using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Links;

/// <summary>
/// <c>GET /Links</c>, rendered — the table the sidebar has always promised.
///
/// The unit tests above prove the mapping; this proves the address, the Razor and the query behind
/// it, which is the trio the missing page was missing.
///
/// The shell assertions live here rather than in a file of their own because this is the only place
/// in the suite where the panel layout is rendered with a session in it: everything else is either
/// anonymous or stops at the ViewResult.
/// </summary>
public class LinksScreenTests
{
    [Fact]
    public async Task The_table_draws_the_tenants_links_the_way_the_comp_does()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4", maxDownloads: 500, downloadCount: 241);

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/Links", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = Text(await response.Content.ReadAsStringAsync());

        html.Should().Contain("لینک‌های اشتراک");
        html.Should().Contain("Q3-Report-Final.pdf");
        html.Should().Contain("/d/kx91mzq4");
        html.Should().Contain("۲۴۱/۵۰۰");
        html.Should().Contain("فعال");
    }

    [Fact]
    public async Task The_page_names_no_google_account()
    {
        // The single most consequential rule in the product: a customer must never learn that their
        // file sits in an account somebody else owns, still less which one.
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        var (accountEmail, driveFileId) = harness.SecretsOf(tenant.Id);

        using var client = harness.NewClient(tenant.Id);
        var html = Text(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));

        html.Should().NotContain(accountEmail);
        html.Should().NotContain(driveFileId);
        html.Should().NotContain("اکانت‌های گوگل", "the pool's own screen is operator-only");
        html.Should().NotContain("drive.google.com");
    }

    [Fact]
    public async Task Tenant_B_is_shown_none_of_tenant_As_links()
    {
        await using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "acme-payroll.zip", "kx91mzq4");
        var globex = harness.SeedTenant("Globex", "globex-notes.pdf", "8vaq2cq1");

        using var client = harness.NewClient(globex.Id);
        var html = Text(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));

        html.Should().Contain("globex-notes.pdf");
        html.Should().NotContain("acme-payroll.zip");
        html.Should().NotContain("kx91mzq4");
    }

    [Fact]
    public async Task A_tenant_with_no_links_gets_the_empty_state_rather_than_an_empty_grid()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using (var db = harness.NewDbContext())
        {
            db.ShareLinks.RemoveRange(db.ShareLinks);
            db.SaveChanges();
        }

        using var client = harness.NewClient(tenant.Id);
        var html = Text(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));

        html.Should().Contain("هنوز لینکی ساخته نشده است.");
    }

    [Fact]
    public async Task The_shell_offers_a_way_out_and_it_is_a_post()
    {
        // A GET sign-out is ended by any «img src» pointing at it and by any browser that prefetches
        // the sidebar. So: a form, a POST, and the antiforgery token that makes it a real one.
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var html = Text(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));

        html.Should().Contain("method=\"post\" action=\"/Identity/Account/Logout\"");
        html.Should().Contain("__RequestVerificationToken");
        html.Should().Contain(">خروج<");

        // And it is the same address Program.cs hands the cookie handler as its LogoutPath, not a
        // link to somewhere that only looks like it.
        html.Should().NotContain("<a class=\"nav-item\" href=\"/Identity/Account/Logout\"");
    }

    [Fact]
    public async Task An_anonymous_caller_is_offered_no_way_out_of_a_session_they_do_not_have()
    {
        await using var harness = new PanelPageHarness();

        using var client = harness.NewClient(tenantId: null);
        var html = Text(await client.GetStringAsync(new Uri("/Identity/Account/Login", UriKind.Relative)));

        // The sign-in page wears the same shell. A sign-out form on it is a control that can only
        // fail, on the one page that has to work.
        html.Should().Contain("brand-mark", "the sign-in page really is rendering the panel layout");
        html.Should().NotContain("/Identity/Account/Logout");
    }

    /// <summary>
    /// The markup with its character references resolved.
    ///
    /// Razor's default encoder escapes everything outside Basic Latin, so «فعال» written by an
    /// expression arrives as <c>&amp;#x641;…</c> while the same word written as literal markup
    /// arrives as itself. Asserting on the decoded text asks what the page says rather than which
    /// side of that line each string happened to fall on.
    /// </summary>
    private static string Text(string html) => WebUtility.HtmlDecode(html);
}
