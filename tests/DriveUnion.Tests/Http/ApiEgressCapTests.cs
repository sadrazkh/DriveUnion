using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Api;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Http;

/// <summary>
/// <b>The monthly traffic allowance, on the route that could always walk around it.</b>
///
/// <para>The public download path has been metered and capped since the allowance was enforced. This
/// one never was, and it said so in a comment: the meter counted what a workspace served to the
/// public, and a customer pulling their own file back was held not to be that.</para>
///
/// <para>The exemption did not survive contact with two facts. Google bills the operator for every
/// byte out of the pool account and has no opinion about who asked for it, so the operator's own
/// egress chart was drawing a subset and calling it the total. And there is no privileged
/// self-retrieval route in this product for the exemption to be consistent with — the panel has no
/// download action at all, and a customer reaches their own file by making a share link, which is
/// metered and capped like every other one. So it was never «your own files are free»; it was «your
/// own files are free if you fetch them with a program», which is a cap in front of the browser and
/// an open door behind it.</para>
/// </summary>
public class ApiEgressCapTests
{
    private const long Allowance = 100_000;

    /// <summary>
    /// <b>The hole, closed.</b> A workspace that has served its month cannot pull the rest through an
    /// API key — and the refusal costs the operator nothing to produce.
    /// </summary>
    [Fact]
    public async Task A_workspace_over_its_allowance_cannot_pull_its_own_file_through_the_api()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await Get(client, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}/content", key);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The half that costs money: a gate that ran after the stream was open would have paid
        // Google for the connection before discovering it should not have.
        harness.Drive.Calls.Should().NotContain(
            call => call.Operation == FakeDriveOperation.OpenDownload,
            "a refusal that reaches Google has already spent the egress it exists to save");

        // And nothing was added to the month for a transfer that never happened.
        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(Allowance);
    }

    /// <summary>
    /// The refusal says when it lifts, and says it as a moment rather than a guess.
    ///
    /// <para>A calendar allowance has an exact reset instant and the server knows it, so a rounded
    /// «try again in about two days» would be a worse answer that also goes stale in a cache. It is
    /// the same header, from the same helper, that the public card and the S3 gateway send.</para>
    /// </summary>
    [Fact]
    public async Task The_refusal_tells_a_program_when_to_come_back()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", monthlyEgressBytes: Allowance);

        harness.SeedTrafficThisMonth(seeded.TenantId, Allowance);

        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await Get(client, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}/content", key);

        var retryAfter = response.Headers.RetryAfter?.Date;

        retryAfter.Should().NotBeNull("a 429 with no Retry-After tells a client to guess");
        retryAfter!.Value.Day.Should().Be(1, "the counter rolls at the start of a calendar month");
        retryAfter.Value.TimeOfDay.Should().Be(TimeSpan.Zero, "and at midnight UTC, which is when it does");
        retryAfter.Value.Should().BeAfter(DateTimeOffset.UtcNow);

        // 429 and not 403. A client that reads 403 goes looking for a permissions fault that does
        // not exist — the key is fine, the scope is fine, and the file is theirs.
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The positive control. Without it every assertion above would pass just as happily on a product
    /// that had stopped serving the API altogether.
    /// </summary>
    [Fact]
    public async Task A_workspace_inside_its_allowance_still_gets_its_bytes()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096), monthlyEgressBytes: Allowance);

        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await Get(client, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}/content", key);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().HaveCount(4096);
    }

    /// <summary>
    /// <b>What was actually served is what is counted</b>, and it lands on the right workspace.
    ///
    /// <para>This is the reporting half of the bug rather than the capping half: before it, the
    /// operator's «what has this product served» chart drew the public path alone, so every byte
    /// pulled through a key was one Google billed for and no screen in the product could account
    /// for.</para>
    /// </summary>
    [Fact]
    public async Task Bytes_pulled_through_a_key_land_on_that_workspaces_month()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096), monthlyEgressBytes: Allowance);

        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await Get(client, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}/content", key);

        await response.Content.ReadAsByteArrayAsync();

        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(
            4096,
            "the bytes that left the pool account are the bytes the operator is billed for");
    }

    /// <summary>
    /// A ranged pull pays for the range it asked for, not for the whole file.
    ///
    /// <para>A program resuming a large download is the ordinary case here, and a meter that charged
    /// the full length every time would turn one interrupted 2 GB transfer into 4 GB of a customer's
    /// month. The public path already counts this way; this asserts the API does too rather than
    /// trusting that it shares the helper.</para>
    /// </summary>
    [Fact]
    public async Task A_resumed_download_is_charged_for_what_it_resumed()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096), monthlyEgressBytes: Allowance);

        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}/content");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Range = new RangeHeaderValue(1000, 1999);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);

        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().HaveCount(1000);

        (await harness.MeteredAsync(seeded.TenantId)).Should().Be(
            1000, "a range costs the operator the range, and the customer should pay for no more");
    }

    /// <summary>
    /// One workspace's traffic is never another's.
    ///
    /// <para>The tenant is taken off the principal the key resolved to and nothing else, so this is
    /// really a test that the id is not coming from the route or from whichever row was read last.
    /// It is cheap and the failure it catches is a customer being billed for a stranger.</para>
    /// </summary>
    [Fact]
    public async Task A_pull_is_charged_to_the_workspace_whose_key_it_was()
    {
        await using var harness = new PublicSiteHarness();
        var mine = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));
        var theirs = harness.SeedLink("9wq0aaz1", content: PublicSiteHarness.TestBytes(4096));

        var key = await MintAsync(harness, mine.TenantId);

        using var client = harness.NewClient();
        using var response = await Get(client, $"/api/v1/files/{FileIdOf(harness, mine.TenantId)}/content", key);

        await response.Content.ReadAsByteArrayAsync();

        (await harness.MeteredAsync(mine.TenantId)).Should().Be(4096);
        (await harness.MeteredAsync(theirs.TenantId)).Should().Be(0);
    }

    private static async Task<string> MintAsync(PublicSiteHarness harness, Guid tenantId)
    {
        await using var db = harness.NewDbContext();
        var store = new ApiTokenStore(db, TimeProvider.System);

        var minted = await store.MintAsync(tenantId, Guid.NewGuid(), "test", ApiScope.Read, null, default);

        return minted.Minted!.Secret;
    }

    private static Guid FileIdOf(PublicSiteHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();

        return db.StoredFiles.AsNoTracking().First(f => f.TenantId == tenantId).Id;
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        return client.SendAsync(request);
    }
}
