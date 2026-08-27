using System.Net;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// <b>The monthly traffic allowance, enforced.</b>
///
/// <para>Every plan in the catalogue sells a monthly egress figure, it was copied onto the workspace
/// row when the plan was applied, and until now <i>nothing in the product compared anything against
/// it</i>: a workspace on a 300 GB tier could serve ten terabytes of the operator's Google egress and
/// the only thing that would ever have noticed is a bill. These tests are the comparison, and each
/// one names the failure it prevents rather than the line it covers.</para>
///
/// <para>Through the real pipeline, because three of the properties here are only true of a request:
/// that the refusal happens <i>before</i> Google is contacted, that it spends none of the link's
/// downloads, and that it is a different answer from the card the other four refusals share.</para>
/// </summary>
public class PublicEgressCapTests
{
    private const long Allowance = 100_000;

    /// <summary>
    /// <b>The whole point.</b> A workspace that has served its month is refused, and refused before
    /// a single byte of Google's egress is spent finding that out.
    /// </summary>
    [Fact]
    public async Task A_workspace_over_its_allowance_is_refused_before_drive_is_opened()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The half that costs money. A gate that ran after the stream was open would have already
        // paid Google for the connection — and on a 214 GB file, for hours of it — so «refused» has
        // to mean «Drive was never asked», the same rule the download-slot reservation follows.
        harness.Drive.Calls
            .Should().NotContain(
                call => call.Operation == FakeDriveOperation.OpenDownload,
                "a refusal that reaches Google has already spent the egress it exists to save");

        // And nothing was billed for a transfer that never happened.
        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(Allowance);
    }

    /// <summary>
    /// The positive control. Without it every assertion in this file would pass on a product that
    /// refused every download in it.
    /// </summary>
    [Fact]
    public async Task A_workspace_inside_its_allowance_still_gets_its_file()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink(
            "kx91mzq4", content: PublicSiteHarness.TestBytes(4096), monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance - 1);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(4096);
    }

    /// <summary>
    /// <b>A transfer that starts under the cap finishes, even though it ends over it.</b>
    ///
    /// <para>One byte of allowance left and four kilobytes to send: the visitor gets all four
    /// thousand and ninety-six of them. That is the decision, not an accident of where the check
    /// sits — cutting a 40 GB download at 99% because a counter crossed a line mid-stream hands the
    /// visitor a corrupt file, hands the customer a support ticket, and saves the operator nothing,
    /// because the bytes were already on the wire when the line was crossed.</para>
    ///
    /// <para>The overage is real and it is recorded. What it stops is the <i>next</i> transfer, which
    /// is the second half of this test and the same rule <c>TenantStorageMeter.SettleAsync</c>
    /// already applies to an upload that came in larger than it declared.</para>
    /// </summary>
    [Fact]
    public async Task A_transfer_that_starts_under_the_cap_is_allowed_to_finish_over_it()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink(
            "kx91mzq4", content: PublicSiteHarness.TestBytes(4096), monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance - 1);

        using var client = harness.NewClient();
        using var served = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync()).Length.Should().Be(
            4096,
            "a transfer admitted under the cap is not truncated when it crosses it");

        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(
            Allowance - 1 + 4096,
            "the overage is recorded rather than hidden — the bytes really did go out");

        // …and the next one is refused, which is where the cap actually bites.
        using var refused = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// <b>A preview is bytes, so a preview is capped.</b>
    ///
    /// <para>It deliberately does not spend one of the link's downloads — a page load is not a
    /// download, and five people looking at a file must not exhaust a link capped at five. That cap
    /// counts deliveries the <i>customer</i> chose to allow. This one counts bytes the <i>operator</i>
    /// buys from Google, the preview route puts them on the wire, and the landing page publishes its
    /// URL — so exempting it would leave the allowance bypassable by anyone willing to request
    /// /preview in a loop against every image and PDF in the product.</para>
    /// </summary>
    [Fact]
    public async Task A_preview_is_refused_too_because_a_preview_is_still_bytes()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink(
            "kx91mzq4",
            fileName: "chart.png",
            mimeType: "image/png",
            monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/preview", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        harness.Drive.Calls.Should().NotContain(call => call.Operation == FakeDriveOperation.OpenDownload);
    }

    /// <summary>
    /// <b>The refusal is not the enumeration card, and that is a decision.</b>
    ///
    /// <para><c>Unavailable</c> collapses revoked, expired, capped and never-existed into one
    /// identical 404 so that a scanner cannot tell a real slug from a dead one. It does not apply
    /// here: by the time this refusal is reached the link has <i>already resolved</i> — active,
    /// unexpired, unspent — and on any other day of the month this same request would have answered
    /// with the file. The oracle a distinct answer is accused of opening is one that a working link
    /// opens by working.</para>
    ///
    /// <para>And the collapse would be a lie. «This link is no longer available» is true of the other
    /// four; this one clears by itself at the turn of the month with nothing done to it, and telling
    /// a visitor otherwise makes them delete the email and makes the customer re-issue a link that
    /// fails identically.</para>
    /// </summary>
    [Fact]
    public async Task The_refusal_says_what_it_is_rather_than_pretending_the_link_is_gone()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        // A slug of the right shape that no row has ever carried — the card the collapse exists for.
        using var client = harness.NewClient();
        using var missing = await client.GetAsync(new Uri("/d/zzzzzzzz/file", UriKind.Relative));
        using var capped = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        capped.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var missingBody = WebUtility.HtmlDecode(await missing.Content.ReadAsStringAsync());
        var cappedBody = WebUtility.HtmlDecode(await capped.Content.ReadAsStringAsync());

        cappedBody.Should().NotBe(missingBody);
        cappedBody.Should().Contain("ترافیک ماهانه‌ی فرستنده تمام شده");
        cappedBody.Should().Contain("لینک هنوز معتبر است");

        // Neither refusal is cacheable. It matters more for this one than for any other on the
        // controller: it is the only refusal in the product guaranteed to stop being true, and a
        // copy of it in a shared cache would outlive the state it describes.
        capped.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// …and the card tells the visitor nothing about the customer.
    ///
    /// <para>The visitor is a stranger who was handed a link. What the sender bought, how much of it
    /// is gone, who they are and which workspace they belong to are between them and the operator —
    /// so the page carries a language and a sentence and no figures at all.</para>
    /// </summary>
    [Fact]
    public async Task The_refusal_puts_none_of_the_customers_commercial_position_on_the_page()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        body.Should().NotContain(seeded.TenantId.ToString(), "a workspace id is a correlator");
        body.Should().NotContain(Allowance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        body.Should().NotContain("100 KB", "the allowance, formatted — still the allowance");
        body.Should().NotContain("Acme", "nor the workspace's name");
        body.Should().NotContain(seeded.GoogleAccountEmail, "and never the pool");

        // The slug is deliberately not on that list. _PublicLayout writes the request's own path
        // into its hreflang alternates, so every page on this site — including the four-cause
        // refusal card — carries it, and it is the address the visitor typed to get here. What must
        // not appear is anything they did not already have.
        body.Should().Contain("kx91mzq4");
    }

    /// <summary>
    /// <b>An over-cap refusal does not spend one of the link's downloads.</b>
    ///
    /// <para>The gate runs before the reservation for exactly this reason. Reserving first and then
    /// refusing would take a slot the visitor never used, and a customer whose links are capped at
    /// five would come back at the turn of the month to find them spent by refusals.</para>
    /// </summary>
    [Fact]
    public async Task Being_refused_costs_the_link_none_of_its_downloads()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", maxDownloads: 5, monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative)))
                .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(0);
    }

    /// <summary>
    /// The header that says when it stops being true.
    ///
    /// <para>Midnight UTC on the first of next month, which is the moment the month window the meter
    /// sums over actually rolls — not a round number of hours picked to look reasonable.</para>
    /// </summary>
    [Fact]
    public async Task The_refusal_says_when_it_lifts()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));

        var now = DateTimeOffset.UtcNow;
        var expected = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

        response.Headers.RetryAfter!.Date.Should().Be(expected);
    }

    /// <summary>
    /// A HEAD probe is still answered, because a probe costs the operator nothing.
    ///
    /// <para>It reaches neither Drive nor the streaming path and it sends no body, so there is no
    /// egress to refuse. Turning it away would break the players that ask for a length before they
    /// open a stream, in exchange for saving zero bytes.</para>
    /// </summary>
    [Fact]
    public async Task A_probe_is_still_answered_because_a_probe_serves_no_bytes()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/d/kx91mzq4/file");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(Allowance);
    }

    /// <summary>
    /// <b>One workspace's overage never refuses another's links.</b>
    ///
    /// <para>The gate takes its workspace from the ticket the slug resolved to, on a route that has
    /// no signed-in user at all. A gate that read the wrong workspace would be a whole product's
    /// downloads going dark because one customer had a busy month, and no test that signs in first
    /// could see it.</para>
    /// </summary>
    [Fact]
    public async Task One_workspace_running_out_does_not_refuse_anothers_links()
    {
        await using var harness = new PublicSiteHarness();

        var spent = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        var fine = harness.SeedLink(
            "zq40mkx9", content: PublicSiteHarness.TestBytes(1024), monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(spent.TenantId, Allowance);

        using var client = harness.NewClient();

        using var refused = await client.GetAsync(new Uri("/d/kx91mzq4/file", UriKind.Relative));
        using var served = await client.GetAsync(new Uri("/d/zq40mkx9/file", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync()).Length.Should().Be(1024);
    }

    /// <summary>
    /// The landing page is not gated, and that is deliberate.
    ///
    /// <para>It contacts nobody and serves no bytes of the file, so there is no egress to refuse —
    /// and the visitor gets the card that tells them what the file is before they press a button
    /// that will explain the refusal precisely. A page that 503'd would leave a stranger with a link
    /// and no sentence at all.</para>
    /// </summary>
    [Fact]
    public async Task The_landing_page_still_renders_for_a_workspace_that_is_out_of_traffic()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);
        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri("/d/kx91mzq4", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("kx91mzq4");
    }
}
