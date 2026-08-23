using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The limiter on <c>/d/*</c> — the only anonymous, expensive, publicly-guessable route in the
/// product (spec §9).
///
/// The numbers come from <c>DriveUnionWebServiceCollectionExtensions</c>: the landing page is a
/// fixed window of 60 per minute, and the stream is a token bucket holding 120 and refilling 60 a
/// minute. Both bursts are spent here in-process with no clock manipulation and no sleeping — the
/// window is a minute wide and these loops take well under a second, so nothing replenishes
/// underneath them.
///
/// One harness per test matters more here than anywhere else: the partition key is the connection's
/// address, which TestServer leaves null, so every request in a container shares one bucket.
/// </summary>
public class PublicRateLimitTests
{
    private const int PageBurst = 60;
    private const int DownloadBurst = 120;

    [Fact]
    public async Task The_landing_page_limiter_rejects_with_429_once_its_sixty_permits_are_spent()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("rl11rl11");

        using var client = harness.NewClient();

        for (var i = 1; i <= PageBurst; i++)
        {
            using var permitted = await client.GetAsync($"/d/{seeded.Slug}");
            permitted.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is inside the window's 60 permits", i);
        }

        using var rejected = await client.GetAsync($"/d/{seeded.Slug}");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // OnRejected copies the limiter's own RetryAfter metadata into the header, so a client that
        // reads it waits for the window instead of hammering.
        rejected.Headers.Should().ContainKey("Retry-After");
    }

    [Fact]
    public async Task The_stream_limiter_rejects_with_429_once_its_bucket_of_a_hundred_and_twenty_is_empty()
    {
        // A bucket rather than a window because scrubbing a video legitimately fires a burst of
        // ranged requests. What the refill actually caps is the sustained rate a scanner needs.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("rl22rl22", content: PublicSiteHarness.TestBytes(16));

        using var client = harness.NewClient();

        for (var i = 1; i <= DownloadBurst; i++)
        {
            using var permitted = await client.GetAsync($"/d/{seeded.Slug}/file");
            permitted.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is inside the bucket of 120", i);
        }

        using var rejected = await client.GetAsync($"/d/{seeded.Slug}/file");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Spending_the_landing_page_budget_does_not_close_the_stream()
    {
        // Two policies, not one, because the two routes cost different things. A visitor who
        // refreshed the card too often must still be able to take the file.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("rl33rl33", content: PublicSiteHarness.TestBytes(16));

        using var client = harness.NewClient();

        for (var i = 0; i <= PageBurst; i++)
        {
            using var page = await client.GetAsync($"/d/{seeded.Slug}");
            page.Dispose();
        }

        using var pageRejected = await client.GetAsync($"/d/{seeded.Slug}");
        pageRejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var stream = await client.GetAsync($"/d/{seeded.Slug}/file");
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task One_visitor_spending_their_permits_does_not_throttle_the_next_one()
    {
        // The limiter partitions on the connection's address. If it did not — the state Program.cs
        // warns about, where every visitor arrives as the OVH proxy — one heavy user would throttle
        // the world and the owner's analytics would read "400 pulls, one visitor".
        await using var harness = new PublicSiteHarness { PartitionByTestRemoteAddress = true };
        var seeded = harness.SeedLink("rl55rl55");

        using var client = harness.NewClient();

        for (var i = 0; i < PageBurst; i++)
        {
            using var page = await Fetch(client, seeded.Slug, "203.0.113.10");
            page.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var spent = await Fetch(client, seeded.Slug, "203.0.113.10");
        spent.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var stranger = await Fetch(client, seeded.Slug, "198.51.100.7");
        stranger.StatusCode.Should().Be(HttpStatusCode.OK, "a second visitor has their own permits");
    }

    private static async Task<HttpResponseMessage> Fetch(HttpClient client, string slug, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{slug}");
        request.Headers.Add(PublicSiteHarness.TestRemoteAddressHeader, address);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_rejected_request_says_nothing_about_whether_the_slug_exists()
    {
        // The limiter answers before the controller runs, so it sees only the address. If a 429 for
        // a live slug differed from a 429 for an unknown one, the throttle itself would become the
        // enumeration oracle the refusal card was built to close.
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("rl44rl44");

        using var client = harness.NewClient();

        for (var i = 0; i < PageBurst; i++)
        {
            using var page = await client.GetAsync("/d/rl44rl44");
            page.Dispose();
        }

        var live = await HttpResponseSnapshot.GetAsync(client, "/d/rl44rl44");
        var unknown = await HttpResponseSnapshot.GetAsync(client, "/d/zzz99999");

        live.StatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
        live.Should().Be(unknown);
    }
}
