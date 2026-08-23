using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// <c>GET /d/{slug}</c> — the page a stranger with a link lands on.
///
/// Slugs here are eight lowercase alphanumerics, which is what <c>SlugGenerator</c> emits and what
/// <c>PublicLinkReader</c> will consent to look up. The comp's six-character <c>/d/kx91mz</c> is
/// rejected before any query runs, so a six-character slug in a test resolves as "unavailable" and
/// the test passes without ever having reached the database.
/// </summary>
public class PublicLandingPageTests
{
    [Fact]
    public async Task An_anonymous_visitor_with_no_cookie_and_no_account_gets_the_landing_page()
    {
        // This is the spec §8 regression test, and the only kind that can see the failure it
        // describes. A global tenant filter fed from the signed-in user would scope this request to
        // Guid.Empty and answer 404 for every live link in the product, while the row sat plainly in
        // the table. A unit test cannot catch it: the failure IS the absence of a session.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", fileName: "quarterly-report.mp4");

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        request.Headers.Contains("Cookie").Should().BeFalse("a link works for someone who has never signed in");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("the public page must not start a session");

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        body.Should().Contain(seeded.FileName);
    }

    [Fact]
    public async Task The_landing_page_names_the_file_with_its_size_and_a_copyable_link()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("ab12cd34", content: PublicSiteHarness.TestBytes(2048));

        using var client = harness.NewClient();
        var body = WebUtility.HtmlDecode(await client.GetStringAsync($"/d/{seeded.Slug}"));

        body.Should().Contain(seeded.FileName);
        body.Should().Contain("2 KB", "the visitor is told how big the file is before they commit to it");

        // The copyable address is built from the configured PublicBaseUrl and not from whatever host
        // the request happened to arrive on — the customer sends this string to someone else. The
        // scheme is dropped on the card, which is what the comp prints.
        var origin = PublicSiteHarness.PublicBaseUrl["https://".Length..];
        body.Should().Contain($"{origin}/d/{seeded.Slug}");
        body.Should().Contain($"/d/{seeded.Slug}/file");
    }

    [Fact]
    public async Task The_landing_page_says_nothing_about_google_and_never_redirects()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("gg77hh88");

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}");

        // No redirect at all: the moment this route answers 3xx, the visitor's browser is holding a
        // googleapis URL and the operator's storage layout is public.
        ((int)response.StatusCode).Should().BeInRange(200, 299);
        response.Headers.Location.Should().BeNull();

        var snapshot = await HttpResponseSnapshot.CaptureAsync(response);
        var searchable = string.Join(
            "\n",
            snapshot.Headers,
            snapshot.Body,
            WebUtility.HtmlDecode(snapshot.Body));

        searchable.Should().NotContain("drive.google.com");
        searchable.Should().NotContain("googleapis.com");
        searchable.Should().NotContain(seeded.DriveFileId, "the Drive file id stays on the server side");
        searchable.Should().NotContain(seeded.GoogleAccountEmail, "a customer must never learn which account holds their file");
    }

    [Fact]
    public async Task The_landing_page_is_never_stored_by_a_cache()
    {
        // The download count on the card moves and the link can be revoked while a copy sits in a
        // proxy. A cached card outlives the revocation it was supposed to reflect.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("nn55mm66");

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }
}
