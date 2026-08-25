using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Metering;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Http;

/// <summary>
/// What a public download costs the operator, counted against the workspace that owns the file.
///
/// <para>Through the real pipeline rather than against <c>TrafficMeter</c> directly, because what is
/// under test here is not the arithmetic — <c>TrafficMeterTests</c> has that — but the two things
/// only the request path can answer: that the bytes counted are the ones that went on the wire, and
/// that they are billed to the right workspace on a route that has no signed-in user at all.</para>
/// </summary>
public class DownloadMeteringTests
{
    [Fact]
    public async Task A_whole_download_is_counted_against_the_file_owners_workspace()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().Be(4096);

        // /d/{slug} is anonymous — there is no cookie, no principal and no tenant on the request.
        // The workspace comes off the ticket, which is the answer to the lookup rather than a
        // parameter to it, and it is the only reason a tenant appears on this path at all.
        Metered(harness, seeded.TenantId).Should().Be(4096);
    }

    [Fact]
    public async Task A_range_is_counted_as_the_bytes_it_asked_for()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Range = new RangeHeaderValue(1000, 1099);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);

        // A hundred bytes, not four thousand. A player scrubbing through a video makes dozens of
        // these, and a meter that charged the file's size for each would report a workspace as
        // having served its library many times over in an afternoon.
        Metered(harness, seeded.TenantId).Should().Be(100);
    }

    [Fact]
    public async Task A_seek_is_counted_even_though_it_is_not_a_download()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096), maxDownloads: 1);

        using var client = harness.NewClient();

        using var seek = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        seek.Headers.Range = new RangeHeaderValue(2000, 2499);

        using var response = await client.SendAsync(seek);
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);

        // The cap is untouched — a mid-file range is somebody continuing a download that has already
        // been paid for, which is what DownloadCounting decides. The egress is not: those bytes came
        // out of Google either way. A meter tied to the cap would under-report every video on the
        // product by however many times it was scrubbed.
        await using var db = harness.NewDbContext();
        var link = await db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == seeded.LinkId);

        link.DownloadCount.Should().Be(0, "a mid-file range does not spend a slot");
        Metered(harness, seeded.TenantId).Should().Be(500, "and it still cost the operator 500 bytes");
    }

    [Fact]
    public async Task Two_workspaces_are_billed_apart()
    {
        await using var harness = new PublicSiteHarness();
        var mine = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(1024));
        var theirs = harness.SeedLink("zq40mkx9", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var one = await client.GetAsync($"/d/{mine.Slug}/file");
        using var two = await client.GetAsync($"/d/{theirs.Slug}/file");

        one.StatusCode.Should().Be(HttpStatusCode.OK);
        two.StatusCode.Should().Be(HttpStatusCode.OK);

        // The line this product is not allowed to cross, restated for money: a customer must never
        // be charged for somebody else's traffic, and there is no signed-in identity on this route
        // to check it against.
        Metered(harness, mine.TenantId).Should().Be(1024);
        Metered(harness, theirs.TenantId).Should().Be(4096);
    }

    [Fact]
    public async Task A_head_probe_costs_nothing()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, $"/d/{seeded.Slug}/file");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Video players ask HEAD for the length before they open a stream. There is no body, so
        // there are no bytes, so there is nothing to charge for — and a meter that counted the
        // promised Content-Length would bill a whole file for opening a page.
        Metered(harness, seeded.TenantId).Should().Be(0);
    }

    /// <summary>Everything the meter wrote for one workspace, whatever day it landed on.</summary>
    private static long Metered(PublicSiteHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();

        return db.TenantUsageDays
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .Sum(u => (long?)u.EgressBytes) ?? 0;
    }
}
