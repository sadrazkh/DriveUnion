using System.Net;
using System.Text.Json;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// What a phone reads when somebody adds this to their home screen.
///
/// <para>Almost everything that goes wrong with a manifest goes wrong silently: the install offer
/// simply does not appear, or the icon is a screenshot of the page, or the label under it is in the
/// wrong language. There is no error anywhere and nothing to notice until somebody looks at a home
/// screen. So the assertions here are about the file being fetchable, being the right media type,
/// and carrying the handful of members an installer actually reads.</para>
/// </summary>
public class WebManifestTests
{
    private const string Path = "/manifest.webmanifest";

    /// <summary>
    /// <b>It is reachable without signing in, and it is JSON of the registered type.</b>
    ///
    /// <para>Anonymous because the sign-in page wears the panel shell and is the first thing a new
    /// customer sees — an install offer that only appears after signing in is one most people never
    /// meet.</para>
    /// </summary>
    [Fact]
    public async Task The_manifest_is_served_to_anybody_as_a_manifest()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(Path, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a 404 here is an app that simply cannot be installed, with nothing said about why");

        response.Content.Headers.ContentType?.MediaType.Should().Be(
            "application/manifest+json",
            "application/json is tolerated by browsers and refused by linters, and it is wrong");
    }

    /// <summary>
    /// The members an installer will not proceed without, and the two that decide it is an app
    /// rather than a bookmark.
    /// </summary>
    [Fact]
    public async Task It_carries_what_an_installer_reads()
    {
        var manifest = await ReadAsync();

        manifest.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        manifest.GetProperty("short_name").GetString().Should().NotBeNullOrWhiteSpace();
        manifest.GetProperty("start_url").GetString().Should().Be("/");
        manifest.GetProperty("scope").GetString().Should().Be("/");

        // Without this it is a bookmark that opens a browser tab, which is the whole difference
        // between what was asked for and what would have shipped.
        manifest.GetProperty("display").GetString().Should().Be("standalone");

        // A stable identity, so changing start_url later updates the installed app instead of
        // putting a second one beside it on the home screen.
        manifest.GetProperty("id").GetString().Should().Be("/");

        manifest.GetProperty("background_color").GetString().Should().Be(BrandColours.LightBackground);
        manifest.GetProperty("theme_color").GetString().Should().Be(BrandColours.LightBackground);
    }

    /// <summary>
    /// <b>The label under the icon follows the panel's language.</b>
    ///
    /// <para>This is the entire reason the manifest is a controller rather than a file in wwwroot,
    /// and the reason the link tag carries <c>crossorigin="use-credentials"</c> — without that
    /// attribute the manifest is fetched with no cookies and this test's distinction cannot happen
    /// in a real browser however well the controller behaves. The tag is asserted separately, below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_is_written_in_the_language_the_panel_is_in()
    {
        var persian = await ReadAsync("fa");
        var english = await ReadAsync("en");

        persian.GetProperty("lang").GetString().Should().Be("fa");
        persian.GetProperty("dir").GetString().Should().Be("rtl");

        english.GetProperty("lang").GetString().Should().Be("en");
        english.GetProperty("dir").GetString().Should().Be("ltr");

        persian.GetProperty("name").GetString().Should().NotBe(english.GetProperty("name").GetString());
        persian.GetProperty("description").GetString()
            .Should().NotBe(english.GetProperty("description").GetString());
    }

    /// <summary>
    /// Persian survives the serialiser.
    ///
    /// <para>The default JSON encoder escapes every non-ASCII character to <c>\uXXXX</c>. That is
    /// valid JSON and an installer reads it correctly, so nothing would break — but this is one of
    /// the few responses in the product somebody opens by hand when an install looks wrong, and a
    /// wall of escapes is the difference between seeing the problem and not.</para>
    /// </summary>
    [Fact]
    public async Task The_persian_name_is_readable_rather_than_escaped()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add("Cookie", LocalizationHarness.CultureCookie("fa"));

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain(UiText.Pwa.ShortName, "the Persian short name should be legible in the file");
        body.Should().NotContain("\\u06", "Persian escaped to \\uXXXX is valid and unreadable");
    }

    /// <summary>
    /// Every icon the manifest promises is actually there, and so is the one only iOS reads.
    ///
    /// <para>A manifest naming an icon that 404s is the failure that produces a home screen entry
    /// showing a screenshot of the page instead of a logo — and nothing anywhere reports it.</para>
    /// </summary>
    [Fact]
    public async Task Every_icon_it_promises_can_be_fetched()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        var manifest = await ReadAsync();
        var icons = manifest.GetProperty("icons").EnumerateArray().ToList();

        icons.Should().HaveCountGreaterThanOrEqualTo(2, "at least the 192 and the 512");

        foreach (var icon in icons)
        {
            var src = icon.GetProperty("src").GetString()!;

            using var response = await client.GetAsync(new Uri(src, UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{src} is named by the manifest");
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }

        // Exactly one maskable, and it must not be the same file as an "any" one: a launcher told an
        // icon is both will mask the un-padded drawing, and the outer discs are the first thing a
        // circular mask takes off.
        var maskable = icons.Where(i => i.GetProperty("purpose").GetString() == "maskable").ToList();

        maskable.Should().ContainSingle();
        icons.Where(i => i.GetProperty("purpose").GetString() == "any")
            .Select(i => i.GetProperty("src").GetString())
            .Should().NotContain(maskable[0].GetProperty("src").GetString());

        // The one iOS reads, which is not in the manifest at all.
        using var apple = await client.GetAsync(new Uri("/icons/apple-touch-icon.png", UriKind.Relative));

        apple.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "without it iOS puts a screenshot of the page on the home screen");
    }

    /// <summary>
    /// The head tags the manifest cannot do without, asserted on the rendered shell.
    ///
    /// <para>Each of these fails silently and separately from the manifest being correct, which is
    /// why they are checked here rather than trusted to review.</para>
    /// </summary>
    [Fact]
    public async Task The_shell_links_the_manifest_the_only_way_that_works()
    {
        await using var harness = new LocalizationHarness();
        var html = await harness.ShellAsync();

        // Without use-credentials the manifest is fetched without the culture cookie, and every
        // install is labelled in the default language whatever the panel is set to.
        html.Should().MatchRegex(
            "<link[^>]*rel=\"manifest\"[^>]*crossorigin=\"use-credentials\"",
            "a manifest link without use-credentials cannot be language-aware");

        // The only icon Safari uses for a home screen, and it has to be a PNG.
        html.Should().MatchRegex(
            "<link[^>]*rel=\"apple-touch-icon\"[^>]*\\.png",
            "iOS ignores an SVG here and screenshots the page instead");

        // Older iOS honours only this; recent iOS reads display: standalone from the manifest.
        html.Should().Contain("apple-mobile-web-app-capable");

        // One theme-color per scheme. The manifest may only carry one, so a panel following the
        // system theme would have the wrong status bar half the time.
        html.Should().Contain($"content=\"{BrandColours.LightBackground}\" media=\"(prefers-color-scheme: light)\"");
        html.Should().Contain($"content=\"{BrandColours.DarkBackground}\" media=\"(prefers-color-scheme: dark)\"");
    }

    private static async Task<JsonElement> ReadAsync(string? culture = null)
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Path);

        if (culture is not null)
        {
            request.Headers.Add("Cookie", LocalizationHarness.CultureCookie(culture));
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
