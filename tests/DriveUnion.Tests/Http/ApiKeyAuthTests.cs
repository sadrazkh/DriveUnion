using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Http;

/// <summary>
/// <c>/api/v1</c>, through the real pipeline with the real authentication.
///
/// <para><b>Why <c>PublicSiteHarness</c> and not one of the panel's.</b> Every other screen harness
/// in this suite swaps the cookie for a header-driven scheme, because what those tests are about is
/// what a page renders for a principal. What <i>this</i> is about is the authentication: a test that
/// minted a principal from a header would prove nothing at all about whether a key works, and would
/// keep passing if the bearer handler were deleted.</para>
/// </summary>
public class ApiKeyAuthTests
{
    [Fact]
    public async Task A_minted_key_reaches_its_own_workspaces_files()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var key = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();
        using var response = await Get(client, "/api/v1/files", key);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(seeded.FileName);
    }

    [Fact]
    public async Task No_key_and_a_wrong_key_are_the_same_answer()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var key = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();

        using var none = await client.GetAsync("/api/v1/files");
        using var garbage = await Get(client, "/api/v1/files", "du_notarealkeyatall");

        // One character off a live key. A response that told this apart from nonsense would be a way
        // to confirm a prefix by brute force.
        using var nearly = await Get(client, "/api/v1/files", key[..^1] + (key[^1] == 'A' ? 'B' : 'A'));

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        garbage.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        nearly.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_challenge_is_a_401_and_never_a_redirect_to_the_sign_in_page()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4");

        using var client = harness.NewClient();
        using var response = await client.GetAsync("/api/v1/files");

        // The cookie scheme answers an unauthenticated request by sending a browser to the sign-in
        // page. A program following that redirect is handed an HTML login form with a 200 on it,
        // which is the least useful possible answer to «your key is not good».
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_revoked_key_stops_working_and_an_expired_one_never_started()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");

        var live = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var expired = await MintAsync(
            harness, seeded.TenantId, ApiScope.Read, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = harness.NewClient();
        (await Get(client, "/api/v1/files", live)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(client, "/api/v1/files", expired)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using (var db = harness.NewDbContext())
        {
            var tokens = new ApiTokenStore(db, TimeProvider.System);
            var mine = await db.ApiTokens.AsNoTracking().FirstAsync(t => t.ExpiresAt == null);

            (await tokens.RevokeAsync(seeded.TenantId, mine.Id, default)).Succeeded.Should().BeTrue();
        }

        // Revocation is the control a customer reaches for when a key has leaked, so it has to bite
        // on the very next request rather than at the end of some cache's life.
        (await Get(client, "/api/v1/files", live)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_read_key_may_look_and_may_not_touch()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var read = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();

        (await Get(client, "/api/v1/files", read)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(client, "/api/v1/usage", read)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", read);

        using var refused = await client.SendAsync(delete);

        // 403 and not 401: the key is good and the caller is known, and what they asked for is not
        // theirs to do. Telling those apart is the one place in this API where the distinction helps
        // rather than leaks — it is the difference between «fix your key» and «use a different one».
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_write_key_may_touch()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var write = await MintAsync(harness, seeded.TenantId, ApiScope.Write);

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", write);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_workspaces_key_cannot_reach_anothers_file()
    {
        await using var harness = new PublicSiteHarness();
        var mine = harness.SeedLink("kx91mzq4");
        var theirs = harness.SeedLink("zq40mkx9");

        var key = await MintAsync(harness, mine.TenantId, ApiScope.Write);
        var theirFile = FileIdOf(harness, theirs.TenantId);

        using var client = harness.NewClient();

        // The line this product is not allowed to cross, restated for a credential that carries its
        // own workspace. 404 and not 403 for all three, or the difference is a way to ask whether a
        // file id exists in somebody else's account.
        (await Get(client, $"/api/v1/files/{theirFile}", key)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get(client, $"/api/v1/files/{theirFile}/content", key)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/files/{theirFile}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        (await client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // …and their file is still there.
        await using var db = harness.NewDbContext();
        (await db.StoredFiles.AsNoTracking().CountAsync(f => f.TenantId == theirs.TenantId && f.DeletedAt == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task Nothing_the_api_returns_names_the_operators_pool()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var key = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();
        using var list = await Get(client, "/api/v1/files", key);
        using var detail = await Get(client, $"/api/v1/files/{FileIdOf(harness, seeded.TenantId)}", key);

        var body = await list.Content.ReadAsStringAsync() + await detail.Content.ReadAsStringAsync();

        // The rule the panel follows, and it binds harder here: a JSON field is forever in a way a
        // rendered table is not, and a customer must never learn that a pool exists.
        body.Should().NotContain(seeded.DriveFileId);
        body.Should().NotContain(seeded.GoogleAccountEmail);

        // …and not the field either, however it were spelled: a null in a response is still a
        // promise that the value exists and might one day be filled in.
        body.Should().NotContainEquivalentOf("googleAccount");
        body.Should().NotContainEquivalentOf("driveFile");
    }

    [Fact]
    public async Task The_content_route_streams_the_bytes_and_honours_a_range()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));
        var key = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var fileId = FileIdOf(harness, seeded.TenantId);

        using var client = harness.NewClient();

        using var whole = await Get(client, $"/api/v1/files/{fileId}/content", key);
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        (await whole.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/files/{fileId}/content");
        ranged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        ranged.Headers.Range = new RangeHeaderValue(0, 99);

        using var part = await client.SendAsync(ranged);

        // A script resuming a large download is the whole reason this passes Range through to Drive
        // untouched rather than reading the file and slicing it.
        part.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await part.Content.ReadAsByteArrayAsync()).Length.Should().Be(100);
    }

    /// <summary>Mints through the real store, because what the tests present has to be a real key.</summary>
    private static async Task<string> MintAsync(
        PublicSiteHarness harness,
        Guid tenantId,
        ApiScope scope,
        DateTimeOffset? expiresAt = null)
    {
        await using var db = harness.NewDbContext();
        var store = new ApiTokenStore(db, TimeProvider.System);

        var minted = await store.MintAsync(tenantId, Guid.NewGuid(), "test", scope, expiresAt, default);

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
