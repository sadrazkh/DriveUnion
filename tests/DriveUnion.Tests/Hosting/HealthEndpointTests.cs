using System.Net;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Tests.Links;
using DriveUnion.Web.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Hosting;

/// <summary>
/// The two addresses Harbora asks this container about itself, answered by the real pipeline.
///
/// <para>What is under test is not «does a route exist» but the distinction the two routes are for.
/// A liveness probe wired to the database is the mistake that turns a four-second Postgres wobble
/// into every replica being killed at once, and the only way to prove it was not made here is to
/// take the database away and ask anyway — which is what half of these tests do.</para>
///
/// <para><see cref="PanelPageHarness"/> rather than a harness of its own: it already boots
/// <c>Program.cs</c> with the loops removed and a SQLite database in place of Postgres, and
/// <c>NewClient(null)</c> is the anonymous caller a probe always is.</para>
/// </summary>
public class HealthEndpointTests
{
    [Fact]
    public async Task Liveness_answers_an_anonymous_caller_with_the_database_unreachable()
    {
        using var harness = new PanelPageHarness();
        using var client = UnreachableDatabase(harness);

        using var response = await client.GetAsync(new Uri(HealthEndpoints.Live, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "liveness says whether this process is up; a probe that fails when Postgres blips has "
            + "the orchestrator kill a container that was perfectly well");

        body.Should().Contain("Healthy");

        // It ran no checks at all, which is the difference between "the process holds a thread and
        // can route a request" and "every dependency answered". Only the first is liveness.
        body.Should().NotContain(HealthEndpoints.DatabaseCheck);
    }

    [Fact]
    public async Task Readiness_refuses_when_the_database_is_unreachable()
    {
        using var harness = new PanelPageHarness();
        using var client = UnreachableDatabase(harness);

        using var response = await client.GetAsync(new Uri(HealthEndpoints.Ready, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "a panel that cannot reach the catalogue cannot answer a single request about a file, "
            + "and should be taken out of rotation rather than killed");

        body.Should().Contain($"\"{HealthEndpoints.DatabaseCheck}\":\"Unhealthy\"");
    }

    [Fact]
    public async Task Readiness_answers_when_the_database_is_reachable()
    {
        using var harness = new PanelPageHarness();
        using var client = harness.NewClient(null);

        using var response = await client.GetAsync(new Uri(HealthEndpoints.Ready, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // All three, and the pool one is the interesting pass: this deployment has no Google account
        // connected yet, and an empty pool is ready on purpose — the operator has to be able to
        // reach the screen that connects the first one. See GooglePoolTokenHealthCheck.
        body.Should().Contain($"\"{HealthEndpoints.DatabaseCheck}\":\"Healthy\"");
        body.Should().Contain($"\"{HealthEndpoints.GooglePoolCheck}\":\"Healthy\"");
        body.Should().Contain($"\"{HealthEndpoints.WorkerLoopCheck}\":\"Healthy\"");
    }

    [Fact]
    public async Task Readiness_refuses_when_no_account_in_the_pool_can_be_refreshed()
    {
        using var harness = new PanelPageHarness();

        // The seeded account's refresh token is the literal string "protected", which no Data
        // Protection key ring will decrypt — the same state a deployment is in when its keys are
        // lost. Every screen still draws and not one byte can be read or written.
        harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);

        using var response = await client.GetAsync(new Uri(HealthEndpoints.Ready, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        body.Should().Contain($"\"{HealthEndpoints.GooglePoolCheck}\":\"Unhealthy\"");
        body.Should().Contain(
            $"\"{HealthEndpoints.DatabaseCheck}\":\"Healthy\"",
            "the database is fine; saying so beside the check that is not is the whole reason this "
            + "endpoint writes more than one word");
    }

    /// <summary>
    /// The rule that matters more than any status code here: these two addresses are anonymous, and
    /// a probe body is a place a deployment describes itself to whoever asked.
    ///
    /// <para>Both failing probes are read, because a failure is when a body grows — the pool check
    /// swallows a sentence that names a pool account, and the database check swallows a driver
    /// message that names a host, a port and a user. Neither may reach the response.</para>
    /// </summary>
    [Fact]
    public async Task Neither_probe_describes_the_deployment_it_is_probing()
    {
        using var harness = new PanelPageHarness();

        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        var (accountEmail, driveFileId) = harness.SecretsOf(tenant.Id);

        using var reachable = harness.NewClient(null);
        using var unreachable = UnreachableDatabase(harness);

        var bodies = new List<string>();

        foreach (var client in new[] { reachable, unreachable })
        {
            foreach (var probe in new[] { HealthEndpoints.Live, HealthEndpoints.Ready })
            {
                using var response = await client.GetAsync(new Uri(probe, UriKind.Relative));

                bodies.Add(await response.Content.ReadAsStringAsync());
            }
        }

        bodies.Should().HaveCount(4);

        foreach (var body in bodies)
        {
            body.Should().NotContain(accountEmail, "the pool account's address is the one fact a "
                + "visitor must never learn, and the token service names it in the sentence it "
                + "throws when a refresh token will not decrypt");

            body.Should().NotContain("@", "no address of any kind belongs in a probe answer");
            body.Should().NotContain(driveFileId);
            body.Should().NotContain(Nowhere, "a connection string names where the data lives");
            body.Should().NotContain("Data Source");
            body.Should().NotContain("Sqlite");
            body.Should().NotContain("Npgsql");
            body.Should().NotContain("Exception");
            body.Should().NotContain("   at ", "a stack trace is a map of the code to whoever asks");
            body.Should().NotContain("DriveUnion.", "not a namespace, not a type, not a version");
        }
    }

    /// <summary>
    /// A database that is not there, without pretending it is somewhere else.
    ///
    /// <para>A path whose parent directory does not exist: SQLite refuses to open it, immediately
    /// and without a network, which is the same answer <c>CanConnectAsync</c> gives for a Postgres
    /// that is down and none of the flakiness of waiting for a name that will not resolve.</para>
    ///
    /// <para>The removal below is <see cref="PanelPageHarness"/>'s, repeated for its reason:
    /// <c>AddDbContext</c> is a <c>TryAdd</c>, so a second call is ignored unless the first is taken
    /// out — including EF's <c>IDbContextOptionsConfiguration&lt;T&gt;</c>, which still carries the
    /// provider the harness put there and throws at resolve time if a second is stacked on it.</para>
    /// </summary>
    private static HttpClient UnreachableDatabase(PanelPageHarness harness)
    {
        var host = harness.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite($"Data Source={Nowhere}"));
        }));

        return host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
    }

    private static readonly string Nowhere =
        Path.Combine(Path.GetTempPath(), $"driveunion-no-such-directory-{Guid.NewGuid():N}", "catalogue.db");
}
