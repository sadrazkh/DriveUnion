using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Core.Metering;
using DriveUnion.Core.Storage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Links;

/// <summary>
/// What a link looks like before anybody clicks it.
///
/// <para>A link pasted into Telegram, WhatsApp or Twitter is fetched by that platform's crawler and
/// drawn as a card, and until these tags existed it drew a bare URL. So the first half of this file
/// is the advertisement: name, size, type, who shared it, and the address.</para>
///
/// <para>The second half is the reason the first half is dangerous, and it is the half worth
/// keeping. <b>Every refusal on the public site renders through the same layout as the download
/// card</b> — «no longer available», the over-traffic card, the abuse form. If any of them carried
/// og: tags, a revoked link would go on unfurling with the file's name in it: the leak would be
/// automatic rather than clicked, it would be shown to everybody in a channel rather than to the
/// one person holding the URL, and it would sit in a third party's cache where nobody here can
/// revoke it twice. Revoking a link is precisely the act of taking that name back.</para>
///
/// <para>The tags are a Razor section that only <c>Views/Public/Download.cshtml</c> declares, so the
/// rule is the shape of the code and not a condition somebody has to keep pointing the right way.
/// These tests assert the outcome anyway — a future <c>_PublicLayout</c> edit could put the tags
/// back in the shared chrome and nothing else in the suite would notice.</para>
/// </summary>
public class LinkUnfurlTests
{
    /// <summary>What <c>PanelPageHarness</c> configures as the public origin.</summary>
    private const string Origin = "https://links.example.test";

    /// <summary>2,048 stored, 1,600 of file — far enough apart to tell which figure was printed.</summary>
    private const long Plaintext = 1600;

    [Fact]
    public async Task A_live_link_unfurls_with_its_name_its_size_and_its_address()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "quarterly-report.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4?lang=en"));

        // The four the spec asks for. og:title is the file's own name, because there is nothing
        // else true to put in a headline.
        markup.Should().Contain(@"<meta property=""og:title"" content=""quarterly-report.pdf"" />");

        // Size · type · who shared it. The separator is the one the card's own assurance line uses,
        // so the preview reads like the page it is a preview of.
        markup.Should().Contain(@"<meta property=""og:description"" content=""4 KB · PDF · Shared by Acme"" />");

        // The canonical address, absolute and built from the configured public base rather than
        // from whatever host the crawler happened to reach — and without the ?lang= that this very
        // request carried, because that is a rendering of the page and not the link.
        markup.Should().Contain($@"<meta property=""og:url"" content=""{Origin}/d/kx91mzq4"" />");

        // Twitter reads og:* for the rest, but not for the card shape: without twitter:card it
        // draws nothing at all.
        markup.Should().Contain(@"<meta name=""twitter:card"" content=""summary"" />");
        markup.Should().Contain(@"<meta name=""twitter:title"" content=""quarterly-report.pdf"" />");
        markup.Should().Contain(@"<meta name=""twitter:description"" content=""4 KB · PDF · Shared by Acme"" />");
    }

    [Fact]
    public async Task The_unfurl_speaks_whichever_language_the_page_was_resolved_into()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "quarterly-report.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);

        // No ?lang= and no Accept-Language is Persian, which is this product's default and the
        // language most of its links are pasted into a chat in.
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4"));

        markup.Should().Contain("به اشتراک گذاشته‌شده توسط Acme");
        markup.Should().Contain(@"<meta property=""og:locale"" content=""fa_IR"" />");

        var english = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4?lang=en"));

        english.Should().Contain("Shared by Acme");
        english.Should().Contain(@"<meta property=""og:locale"" content=""en_US"" />");
    }

    [Fact]
    public async Task An_image_points_its_picture_at_this_sites_own_preview_route()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "beach.png", "kx91mzq4");

        RetypeAs(harness, tenant.Id, "image/png");

        using var client = harness.NewClient(null);
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4?lang=en"));

        // The same URL the card already puts in an <img src> for every visitor, absolute so a
        // crawler can fetch it. Never storage: a googleusercontent address in an unfurl would put
        // the operator's pool account into a third party's logs.
        markup.Should().Contain($@"<meta property=""og:image"" content=""{Origin}/d/kx91mzq4/preview"" />");
        markup.Should().Contain($@"<meta name=""twitter:image"" content=""{Origin}/d/kx91mzq4/preview"" />");
        markup.Should().NotContain("googleusercontent");
        markup.Should().NotContain("drive.google.com");

        // A picture changes the card's shape, and saying so is the difference between a photo and a
        // thumbnail the size of a favicon.
        markup.Should().Contain(@"<meta name=""twitter:card"" content=""summary_large_image"" />");
    }

    [Fact]
    public async Task A_file_with_nothing_safe_to_show_unfurls_without_a_picture_rather_than_with_a_placeholder()
    {
        using var harness = new PanelPageHarness();

        // A PDF: previewable in a frame on the page, and still not a picture. Inventing a generic
        // per-type card for it would be a picture of nothing dressed as a thumbnail — real
        // thumbnails are their own piece of work and this is not it.
        harness.SeedTenant("Acme", "contract.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);
        var markup = await client.GetStringAsync("/d/kx91mzq4?lang=en");

        markup.Should().NotContain("og:image");
        markup.Should().NotContain("twitter:image");
        markup.Should().Contain(@"<meta name=""twitter:card"" content=""summary"" />");
    }

    [Fact]
    public async Task A_locked_file_still_unfurls_with_its_name_and_its_size_and_no_picture()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "passport.pdf", "kx91mzq4");

        Lock(harness, tenant.Id);

        using var client = harness.NewClient(null);
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4?lang=en"));

        // The link is live and the card renders; that the server cannot read the bytes changes
        // nothing about the two facts the recipient needs before they open it.
        markup.Should().Contain(@"<meta property=""og:title"" content=""passport.pdf"" />");

        // 1.6 KB is the file; 4 KB is the ciphertext Drive holds. The unfurl prints the same figure
        // the card does, because the two disagreeing would be a defect nobody could explain from
        // either of them alone.
        markup.Should().Contain("1.6 KB");
        markup.Should().NotContain(@"content=""4 KB");

        // And no picture, because there is nothing to draw: the bytes behind /preview are
        // ciphertext, and the route refuses an encrypted file outright.
        markup.Should().NotContain("og:image");
        markup.Should().NotContain("twitter:image");
    }

    [Fact]
    public async Task The_senders_note_stays_on_the_page_and_off_the_unfurl()
    {
        const string Note = "the passphrase is in the other email";

        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "contract.pdf", "kx91mzq4");

        Annotate(harness, tenant.Id, Note);

        using var client = harness.NewClient(null);
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/d/kx91mzq4?lang=en"));

        // It is on the card, which is the whole point of it — a note is a message to whoever opens
        // the link.
        markup.Should().Contain(Note);

        // And nowhere in the tags. An unfurl is drawn automatically to everybody in a channel and
        // cached by the platform: the sender chose one reader, and this is the difference between
        // them being right about that and not.
        Tags(markup).Should().NotContain(Note);
    }

    [Fact]
    public async Task The_refusal_card_carries_no_tags_at_all()
    {
        using var harness = new PanelPageHarness();

        // Well formed and never issued. The same card a revoked, an expired and a spent link get.
        using var client = harness.NewClient(null);
        using var response = await client.GetAsync("/d/zzzzzzzz");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var markup = await response.Content.ReadAsStringAsync();

        // The whole assertion, deliberately blunt: not «no og:title», not «no file name» — no og:
        // anywhere on the page. Anything narrower passes the day somebody adds a fifth tag.
        markup.Should().NotContain("og:");
        markup.Should().NotContain("twitter:");
    }

    [Fact]
    public async Task A_revoked_link_stops_unfurling_with_the_name_it_used_to_carry()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "passport-scan.pdf", "kx91mzq4", isActive: false);

        using var client = harness.NewClient(null);
        using var response = await client.GetAsync("/d/kx91mzq4?lang=en");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var markup = await response.Content.ReadAsStringAsync();

        markup.Should().NotContain("og:");
        markup.Should().NotContain("twitter:");

        // The point of the paragraph above, stated as a fact about one file: revoking took the name
        // back, and an unfurl that still printed it would be handing it out to a whole channel.
        markup.Should().NotContain("passport-scan.pdf");
    }

    [Fact]
    public async Task The_over_traffic_card_carries_no_tags_either()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "quarterly-report.pdf", "kx91mzq4");

        SpendTheMonthsTraffic(harness, tenant.Id);

        using var client = harness.NewClient(null);
        using var response = await client.GetAsync("/d/kx91mzq4/file");

        // The one refusal that is deliberately told apart from the other four, and it is still a
        // refusal: the link resolved, and then the workspace's month ran out.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var markup = await response.Content.ReadAsStringAsync();

        markup.Should().NotContain("og:");
        markup.Should().NotContain("twitter:");

        // This card is reached for a link that is alive and will serve the file again on the first
        // of the month — so «it says nothing» is a choice, not a consequence of the link being
        // dead. A visitor who is refused today must not have the file named at them in a channel.
        markup.Should().NotContain("quarterly-report.pdf");
    }

    [Fact]
    public async Task The_abuse_form_carries_no_tags_either()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "quarterly-report.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);

        // It renders for any slug, real or not, on purpose — a form that only appeared for live
        // links would answer «does this exist» to anybody who asked. Tags on it would answer the
        // same question with the file's name attached, for the one slug that is real.
        var real = await client.GetStringAsync("/d/kx91mzq4/report");
        var invented = await client.GetStringAsync("/d/zzzzzzzz/report");

        real.Should().NotContain("og:");
        real.Should().NotContain("twitter:");
        real.Should().NotContain("quarterly-report.pdf");

        invented.Should().NotContain("og:");
        invented.Should().NotContain("twitter:");
    }

    /// <summary>Everything between the meta tags and nothing else, so a note can be looked for there alone.</summary>
    private static string Tags(string markup) => string.Join(
        "\n",
        Regex
            .Matches(markup, "<meta[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Value));

    /// <summary>The one file this tenant has, retyped — the harness seeds every file as a PDF.</summary>
    private static void RetypeAs(PanelPageHarness harness, Guid tenantId, string mimeType)
    {
        using var db = harness.NewDbContext();

        var file = db.StoredFiles.First(f => f.TenantId == tenantId);
        file.MimeType = mimeType;

        db.SaveChanges();
    }

    private static void Annotate(PanelPageHarness harness, Guid tenantId, string note)
    {
        using var db = harness.NewDbContext();

        var link = db.ShareLinks.First(l => l.TenantId == tenantId);
        link.Note = note;

        db.SaveChanges();
    }

    /// <summary>
    /// A header over the file, so <c>PublicLinkReader</c> answers with one and the page renders the
    /// unlock card instead of a download button. The values are shaped like the real ones and mean
    /// nothing — no test here decrypts anything.
    /// </summary>
    private static void Lock(PanelPageHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();

        var file = db.StoredFiles.AsNoTracking().First(f => f.TenantId == tenantId);

        db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = tenantId,
            Scheme = 1,
            SegmentSize = 1024 * 1024,
            NoncePrefix = "AAAAAAAAAAA=",
            PlaintextLength = Plaintext,
            KdfSalt = "BBBBBBBBBBBBBBBBBBBBBB==",
            KdfIterations = 600_000,
            WrappedKey = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.SaveChanges();
    }

    /// <summary>
    /// The workspace past its month's egress, written as the roll-up row the meter would have left
    /// behind. Cheaper than moving the bytes, and what the arithmetic does has its own tests.
    /// </summary>
    private static void SpendTheMonthsTraffic(PanelPageHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();

        db.Tenants.Single(t => t.Id == tenantId).MonthlyEgressBytes = 1024;

        // Dated in UTC, which is the clock the month window is computed in — a row stamped in the
        // server's own zone falls out of the window on the first and the last of the month, in one
        // direction on some machines and the other on others.
        db.TenantUsageDays.Add(new TenantUsageDay
        {
            TenantId = tenantId,
            Day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
            EgressBytes = 4096,
            Downloads = 1,
        });

        db.SaveChanges();
    }
}
