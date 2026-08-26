using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The download page as a page, and the route that lets it draw one.
///
/// <para><c>PreviewRulesTests</c> holds the rule. This holds what the rule is worth: that the page
/// and the route read the same one, that the route is not a way around a link's cap or around the
/// list of types it is safe to render, and that a preview is not counted as a download.</para>
/// </summary>
public class PublicPreviewTests
{
    [Fact]
    public async Task An_image_is_drawn_on_the_card_from_our_own_origin()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4", fileName: "chart.png", mimeType: "image/png");

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/d/kx91mzq4?lang=en");

        markup.Should().Contain(@"src=""/d/kx91mzq4/preview""");

        // Never storage's own thumbnail. A googleusercontent.com URL on this page would put the
        // operator's account into the visitor's network log, which is the one thing this product
        // may not do — and it is the first shortcut anybody reaches for when building a preview.
        markup.Should().NotContain("googleusercontent");
        markup.Should().NotContain("drive.google.com");
    }

    [Fact]
    public async Task The_bytes_come_back_inline_and_typed()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", fileName: "chart.png", mimeType: "image/png");

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");

        // The type is echoed from what a browser said at upload, so the browser on this end must not
        // be allowed to disagree with it and pick something else out of the bytes.
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which
            .Should().Be("nosniff");

        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);
    }

    [Fact]
    public async Task The_ordinary_download_is_still_an_attachment()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4", fileName: "chart.png", mimeType: "image/png");

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        // Inline belongs to one route and one list. The button on the page is for keeping the file.
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
    }

    [Fact]
    public async Task A_type_that_could_run_here_is_refused_by_the_route_and_not_only_by_the_page()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4", fileName: "note.html", mimeType: "text/html");

        using var client = harness.NewClient();

        var markup = await client.GetStringAsync("/d/kx91mzq4?lang=en");
        markup.Should().NotContain("/d/kx91mzq4/preview");

        // The page not linking to it is not the defence — a URL is something anybody can type. The
        // route asks the same question again, and this is the answer that matters: an inline
        // text/html on this origin is script running against whoever opened the link.
        using var refused = await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative));

        // The same 404 card a revoked, expired or never-existed slug gets — nothing here may tell a
        // scanner it found something real and merely un-previewable.
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var body = await refused.Content.ReadAsStringAsync();
        body.Should().NotContain("<!-- the file -->");
        body.Should().Contain("<html", "the refusal card is a page, not the file");
    }

    [Fact]
    public async Task A_big_file_is_offered_rather_than_previewed()
    {
        await using var harness = new PublicSiteHarness();

        // One byte past the ceiling, recorded on the row. The bytes behind it are small — the check
        // is on what the catalogue says, before anything is read.
        harness.SeedLink("kx91mzq4", fileName: "film.mp4", mimeType: "video/mp4", sizeBytes: 26 * 1024 * 1024 + 1);

        using var client = harness.NewClient();

        (await client.GetStringAsync("/d/kx91mzq4?lang=en")).Should().NotContain("/d/kx91mzq4/preview");

        using var refused = await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative));
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("chart");
        refused.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Looking_at_a_file_does_not_spend_a_download()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink(
            "kx91mzq4", fileName: "chart.png", mimeType: "image/png", maxDownloads: 1);

        using var client = harness.NewClient();

        // Three page loads' worth of previews against a link that permits one download.
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(0);

        // And the one download the owner allowed is still there to be taken.
        using var taken = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));
        taken.StatusCode.Should().Be(HttpStatusCode.OK);

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task A_spent_link_stops_previewing_too()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink(
            "kx91mzq4",
            fileName: "chart.png",
            mimeType: "image/png",
            maxDownloads: 1,
            downloadCount: 1);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative));

        // The cap is not consulted per preview, but the link's availability is — one Evaluate governs
        // both routes. Without this, a cap somebody set would stop the button and leave the bytes
        // reachable for ever through the other URL.
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("<html");
    }

    [Fact]
    public async Task The_note_and_the_sender_reach_the_page_as_written()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4", note: "فاکتور مرداد — رمز را جدا می‌فرستم.");

        using var client = harness.NewClient();

        // Decoded, because Razor's default encoder writes every non-ASCII character as a numeric
        // reference — «ف» is «&#x0641;» in the response and «ف» in the browser. The escaping test
        // below reads the raw body instead, which is the whole point of it.
        var markup = WebUtility.HtmlDecode(
            await client.GetStringAsync("/d/kx91mzq4?lang=fa"));

        markup.Should().Contain("فاکتور مرداد");
        markup.Should().Contain("Acme", "a page that will not say who sent the file is one nobody trusts");
    }

    [Fact]
    public async Task A_note_cannot_put_markup_on_the_page()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4", note: "<img src=x onerror=\"alert(1)\">");

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/d/kx91mzq4?lang=en");

        // The one string on this page written by one person and read by another. Razor encodes it
        // because it is interpolated rather than raw, and this is the test that says so out loud —
        // a later refactor to Html.Raw for the sake of line breaks would be caught here.
        markup.Should().NotContain("<img src=x");
        markup.Should().Contain("&lt;img src=x");
    }

    [Fact]
    public async Task A_link_with_nothing_written_on_it_shows_no_note_at_all()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4");

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/d/kx91mzq4?lang=en");

        // Not an empty box with a border around it. «No note» has to be nothing on the page.
        markup.Should().NotContain("sender-note");
    }
}
