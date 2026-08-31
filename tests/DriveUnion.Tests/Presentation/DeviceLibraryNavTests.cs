using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Links;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The way in to «ذخیره‌شده روی این دستگاه».
///
/// <para>The screen at <c>/files/offline</c> worked for a long time with no route to it but the
/// address bar, which for a customer is indistinguishable from its not being there. Nobody guesses a
/// URL to find out where the gigabytes on their phone went, or which film will still play on a
/// flight — they look at the menu, which is the reasoning already written beside «زباله‌دان» in the
/// layout and was simply not applied to its other neighbour.</para>
///
/// <para>So the item is asserted from the sidebar rather than from the page: a link in a body is a
/// signpost somebody has to already be standing next to, and the whole defect was that there was
/// nowhere to stand.</para>
/// </summary>
public class DeviceLibraryNavTests
{
    /// <summary>The sidebar only, so a link in the page body is not mistaken for a menu item.</summary>
    private static readonly Regex Sidebar = new(
        """<nav class="app-sidebar".*?</nav>""",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    /// <summary>How the shell marks the row the reader is standing on.</summary>
    private const string Active = "nav-item is-active";

    [Fact]
    public async Task A_signed_in_customer_is_offered_the_device_library_from_the_menu()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);

        var nav = await SidebarAsync(client, "/");

        nav.Should().Contain(
            "href=\"/files/offline\"",
            "the screen was reachable only by typing its address, which is the same as absent");

        // The row has words on it as well as a destination. Asserted because the label is a second,
        // shorter wording than the screen's own heading — see UiText.Shell.OnThisDevice — and a
        // renamed entry would leave a nav item that is a dot and nothing else.
        nav.Should().Contain("روی دستگاه");
    }

    /// <summary>
    /// And nothing of the kind for somebody with no session.
    ///
    /// <para>The sign-in page wears this same shell, so the whole menu is inside
    /// <c>@if (isSignedIn)</c>: a nav item that can only challenge is a control that does nothing.
    /// Fetched from the sign-in address because it is the one page an anonymous caller can be
    /// answered with — every other route in the panel challenges before it draws a sidebar at
    /// all.</para>
    /// </summary>
    [Fact]
    public async Task A_signed_out_reader_is_offered_it_nowhere()
    {
        using var harness = new PanelPageHarness();

        using var client = harness.NewClient(null);

        var nav = await SidebarAsync(client, "/Identity/Account/Login");

        nav.Should().NotContain("/files/offline");
        nav.Should().NotContain("روی دستگاه");
    }

    /// <summary>
    /// One lit row at a time, across the two items that share a controller.
    ///
    /// <para>«فایل‌ها» and «روی دستگاه» are both FilesController, and the shell decides what is
    /// marked from the controller's name — so the obvious way to add this item lights both rows on
    /// both screens. Nothing breaks; the menu just stops answering «where am I», which is the quiet
    /// half of the distrust a dead nav item earns.</para>
    /// </summary>
    [Fact]
    public async Task The_two_file_screens_light_one_row_each()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);

        var list = await SidebarAsync(client, "/files");
        var device = await SidebarAsync(client, "/files/offline");

        Lit(list).Should().Be(1, "the file list is one place, not two");
        Lit(device).Should().Be(1, "so is the device library");

        // Which one, and not merely how many: a pair that swapped their conditions would satisfy the
        // counts above on both screens and be wrong on both.
        Row(device, "/files/offline").Should().Contain(Active);
        Row(device, "/Files").Should().NotContain(Active);
        Row(list, "/Files").Should().Contain(Active);
        Row(list, "/files/offline").Should().NotContain(Active);
    }

    /// <summary>The sidebar the shell drew for this caller on this page, HTML-decoded.</summary>
    private static async Task<string> SidebarAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a page that did not render says nothing about what its menu offers ({path})");

        // Decoded, because Razor's encoder writes everything outside Basic Latin as a numeric
        // character reference — so an assertion against «روی دستگاه» in the raw markup passes on a
        // page that says it and on a page that does not.
        var markup = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        var nav = Sidebar.Match(markup);
        nav.Success.Should().BeTrue($"the shell draws a sidebar on {path}");

        return nav.Value;
    }

    /// <summary>How many rows the menu is claiming the reader is on.</summary>
    private static int Lit(string nav) =>
        Regex.Matches(nav, Regex.Escape(Active), RegexOptions.None, TimeSpan.FromSeconds(5)).Count;

    /// <summary>The opening tag of the anchor pointing at <paramref name="href"/>.</summary>
    private static string Row(string nav, string href)
    {
        var row = Regex.Match(
            nav,
            $"""<a[^>]*\shref="{Regex.Escape(href)}"[^>]*>""",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        row.Success.Should().BeTrue($"the menu has a row pointing at {href}");

        return row.Value;
    }
}
