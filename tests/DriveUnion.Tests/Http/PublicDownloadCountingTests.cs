using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The counter a customer's download cap is spent from, proved through the wire rather than through
/// <c>DownloadCounting</c>.
///
/// The rule itself already has unit tests. What these prove is that the controller applies it to the
/// header the client actually sent, and that the number lands in the database — a controller that
/// forwarded the Range to Drive but counted every request would pass every unit test in the product
/// and still bill twenty downloads for one person scrubbing a video.
/// </summary>
public class PublicDownloadCountingTests
{
    [Fact]
    public async Task A_request_with_no_range_header_counts_as_one_download()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn11cn11", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(1, "the audit trail has to agree");
    }

    [Fact]
    public async Task A_range_that_starts_at_byte_zero_counts_as_one_download()
    {
        // "bytes=0-" is a client asking for the whole file the polite way. It is a download.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn22cn22", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=0-");

        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task The_one_byte_probe_a_video_element_sends_does_not_count()
    {
        // "bytes=0-0" is how a <video> discovers the length and whether ranges work. The real
        // request follows immediately behind it, so counting the probe bills every playback twice.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn33cn33", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=0-0");

        using var response = await client.SendAsync(request);
        (await response.Content.ReadAsByteArrayAsync()).Should().HaveCount(1);

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(0);
    }

    [Fact]
    public async Task A_mid_file_range_does_not_count()
    {
        // One viewer scrubbing through a video otherwise burns twenty of a customer's five hundred.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn44cn44", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=512-767");

        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
    }

    [Fact]
    public async Task Scrubbing_a_video_costs_one_download_and_not_one_per_seek()
    {
        // The whole rule, end to end, as a player actually behaves: a probe, the opening request,
        // and then a seek for every place the viewer dragged the scrubber to.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn55cn55", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();

        foreach (var range in new[] { "bytes=0-0", "bytes=0-", "bytes=1024-2047", "bytes=2048-3071", "bytes=-512" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
            request.Headers.Add("Range", range);

            using var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
        }

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task The_landing_page_does_not_count_as_a_download()
    {
        // Opening the card is not taking the file, and a link with a cap of one would otherwise be
        // spent by the page that offers it.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn66cn66");

        using var client = harness.NewClient();
        await client.GetStringAsync($"/d/{seeded.Slug}");

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
    }
}
