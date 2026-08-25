using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Api;
using DriveUnion.Infrastructure.Persistence.Repositories;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The two doors do not open each other's rooms.
///
/// <para>The panel authenticates with a cookie and the API with a bearer key, and each policy names
/// its own scheme. Neither of those facts is visible at a call site: a controller says
/// <c>[Authorize(Policy = …)]</c> and the scheme is decided somewhere else entirely. So it is
/// asserted, because the failure mode is silent in both directions — a browser session quietly
/// reaching an API route that never expected one, or a key reaching a form post that assumed a CSRF
/// token had been checked.</para>
/// </summary>
public class ApiSurfaceSeparationTests
{
    [Fact]
    public async Task A_key_cannot_reach_the_panels_own_routes()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var key = await MintAsync(harness, seeded.TenantId);

        using var client = harness.NewClient();

        // The panel's screens, its island's JSON, and its upload API. All three are behind the
        // cookie's scheme, and a key presenting itself at any of them is not a caller they know.
        foreach (var path in new[] { "/files", "/api/files", "/keys" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().NotBe(
                HttpStatusCode.OK,
                $"{path} belongs to the browser session, and a key must not be a way in");
        }
    }

    [Fact]
    public async Task An_unauthenticated_browser_cannot_reach_the_api()
    {
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("kx91mzq4");

        using var client = harness.NewClient();

        // Every /api/v1 route, with nothing presented. The list is written out rather than
        // discovered, so a route added later without a policy is a route this test does not cover —
        // and ApiRouteTests below is what covers that.
        foreach (var path in new[] { "/api/v1/files", "/api/v1/folders", "/api/v1/usage" })
        {
            using var response = await client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} is bearer-only");
        }
    }

    /// <summary>
    /// Every action under <c>/api/v1</c> carries one of the API's two policies.
    ///
    /// <para>Read by reflection rather than by pressing each route, because the thing worth
    /// defending against is the one nobody thought to press: an action added next month with no
    /// attribute at all inherits the application's default, and the application's default is the
    /// cookie. It would be reachable from a browser and unreachable from a key — the exact opposite
    /// of what its author intended, with nothing failing.</para>
    /// </summary>
    [Fact]
    public void Every_api_action_names_an_api_policy()
    {
        var actions = typeof(Web.Controllers.Api.V1FilesController).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "DriveUnion.Web.Controllers.Api")
            .SelectMany(t => t.GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
            .ToList();

        actions.Should().NotBeEmpty("the API has actions; finding none is a broken test");

        var unguarded = actions
            .Where(m => m
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .All(a => a.Policy is not (Web.Security.ApiPolicies.Read or Web.Security.ApiPolicies.Write)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        unguarded.Should().BeEmpty(
            "an API action with no policy of its own falls back to the application default, which "
            + "is the cookie — reachable from a browser and not from a key, which is backwards");
    }

    private static async Task<string> MintAsync(PublicSiteHarness harness, Guid tenantId)
    {
        await using var db = harness.NewDbContext();
        var store = new ApiTokenStore(db, TimeProvider.System);

        var minted = await store.MintAsync(tenantId, Guid.NewGuid(), "test", ApiScope.Write, null, default);

        return minted.Minted!.Secret;
    }
}
