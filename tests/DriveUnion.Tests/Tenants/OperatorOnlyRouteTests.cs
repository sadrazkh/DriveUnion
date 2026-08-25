using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Tests.Hosting;
using DriveUnion.Web.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// A customer who types an operator address is refused by the policy, on every route, with nothing
/// having happened.
///
/// <para>The workspace list names every customer, and the workspace page creates accounts and takes
/// them away. A tenant user reaching either would be reading the operator's whole customer book and
/// holding the keys to it. Hiding the nav link is not a control, so the test is about the status
/// code and never about the markup.</para>
///
/// <para><b>The route list is generated, not hand-written.</b> It comes out of the test host's own
/// <c>EndpointDataSource</c>, so a route added to <c>TenantsController</c> in six months is covered
/// by this test on the day it is added rather than on the day somebody remembers to list it.</para>
/// </summary>
[Collection(TenantHostCollection.Name)]
public class OperatorOnlyRouteTests
{
    [Fact]
    public async Task Every_operator_tenant_route_refuses_a_tenant_user_with_403()
    {
        using var harness = new OperatorRouteHarness(asOperator: false);
        using var client = harness.NewClient();

        var routes = harness.OperatorTenantRoutes();

        // A floor rather than an exact list: the generated set is the point, and a new route joining
        // it should be covered rather than argued about. The floor is here because a reflection bug
        // that found nothing would otherwise make this test pass loudest of all.
        routes.Should().HaveCountGreaterThanOrEqualTo(
            7, "the list, the create, the workspace page, the member create, and disable/enable/reset");

        // Named so the failure message is readable when one of them is not what it claims to be.
        routes.Select(r => $"{r.Method} {r.Path}").Should().OnlyHaveUniqueItems();

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);

            // 403 and not a redirect: the caller is authenticated and simply lacks the capability.
            // Nothing about another tenant's data is revealed, and nothing is asked of them.
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"{method} {path} is operator-only and the caller is a customer");
        }

        // Refused by the authorisation middleware before any filter runs, so no workspace and no
        // account can have been created along the way.
        await using var db = harness.NewDbContext();

        (await db.Tenants.AnyAsync()).Should().BeFalse();
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// The other half, and it is not optional: without it the test above would pass just as happily
    /// against a controller that had been deleted, or against routes that 404 for everybody.
    /// </summary>
    [Fact]
    public async Task The_same_routes_answer_for_an_operator()
    {
        using var harness = new OperatorRouteHarness(asOperator: true);
        using var client = harness.NewClient();

        foreach (var (method, path) in harness.OperatorTenantRoutes())
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);

            // What they answer varies — a page, a 404 for a workspace id that names nobody, a
            // redirect after a write. What matters is that the policy is not what stopped them.
            response.StatusCode.Should().NotBe(
                HttpStatusCode.Forbidden, $"{method} {path} is the operator's own screen");
        }
    }

    [Fact]
    public async Task An_anonymous_visitor_is_challenged_rather_than_shown_the_customer_book()
    {
        using var harness = new OperatorRouteHarness(asOperator: null);
        using var client = harness.NewClient();

        foreach (var (method, path) in harness.OperatorTenantRoutes())
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized, $"{method} {path} is behind an authenticated policy");
        }
    }
}

/// <summary>
/// The panel's real pipeline with the authentication scheme replaced wholesale, so a policy refusal
/// is observable as the status code it is.
///
/// <para>Identity's cookie handler turns a refusal into a 302 to <c>AccessDenied</c>, which is right
/// for a browser and useless for this test: it makes "refused" and "redirected somewhere" the same
/// observation. A bare scheme's default <c>Forbid</c> is a 403 and its default <c>Challenge</c> is a
/// 401, which is exactly the pair under test. The policies themselves are the product's own, from
/// <c>AddDriveUnionWeb</c>.</para>
/// </summary>
/// <param name="asOperator">
/// True mints an operator, false mints a customer with a tenant and without the operator claim, and
/// null mints nobody. The customer is authenticated on purpose: an anonymous request is refused by
/// <c>RequireAuthenticatedUser</c> and would prove nothing about the claim that is actually load
/// bearing.
/// </param>
public sealed class OperatorRouteHarness(bool? asOperator) : WebApplicationFactory<Program>
{
    private const string TestScheme = "DriveUnion.TestPrincipal";

    /// <summary>A workspace id in a path that has one. It names nobody, and it never gets that far.</summary>
    private static readonly Guid SomeTenantId = new("2b1f6a2e-9d5c-4a41-8f0e-6d1c3a7b4e55");

    private static readonly Guid SomeUserId = new("7c4e1b90-3a2d-4f88-9e11-5b0a6c2d8f31");

    private readonly SqliteConnection connection = OpenSchema();

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    /// <summary>
    /// Every route under <c>/operator/tenants</c> the host actually has, with its ids filled in.
    ///
    /// <para>Read out of <see cref="EndpointDataSource"/> rather than listed, because a list is a
    /// second place to forget something — and the thing being forgotten would be an unprotected way
    /// into the operator's customer book.</para>
    /// </summary>
    public IReadOnlyList<(string Method, string Path)> OperatorTenantRoutes()
    {
        var endpoints = Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var routes = new List<(string, string)>();

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = endpoint.RoutePattern.RawText;

            if (template is null) continue;

            var normalised = template.StartsWith('/') ? template : "/" + template;

            if (!normalised.StartsWith("/operator/tenants", StringComparison.Ordinal)) continue;

            var methods = endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                ?? ["GET"];

            var path = normalised
                .Replace("{tenantId:guid}", SomeTenantId.ToString(), StringComparison.Ordinal)
                .Replace("{userId:guid}", SomeUserId.ToString(), StringComparison.Ordinal);

            foreach (var method in methods) routes.Add((method, path));
        }

        return routes;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));

        var principal = asOperator;

        builder.ConfigureTestServices(services =>
        {
            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

            // The Telegram drainer, poller and sweeper open their own scopes on a background timer.
            // Against a shared SQLite :memory: connection that turns into "database is locked" in
            // the middle of a transaction these tests do open — a failure about the harness rather
            // than about the product. Nothing here has a bot, a chat or an outbox row.
            // Every loop and not only Telegram's, for the reason TestHostServices sets out: the one
            // this host misses is the one that lands on somebody else's teardown.
            services.RemoveEveryBackgroundLoop();

            services.AddDriveUnionTenancy();

            services.Configure<DriveUnionSeedOptions>(options =>
            {
                options.OperatorEmail = null;
                options.OperatorPassword = null;
                options.TenantSlug = null;
                options.TenantUserEmail = null;
                options.TenantUserPassword = null;
            });

            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = TestScheme;
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                })
                .AddScheme<TestPrincipalOptions, TestPrincipalHandler>(
                    TestScheme, options => options.IsOperator = principal);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }

    private static SqliteConnection OpenSchema()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

        schema.Database.EnsureCreated();

        return connection;
    }

    private sealed class TestPrincipalOptions : AuthenticationSchemeOptions
    {
        /// <summary>Null is nobody at all.</summary>
        public bool? IsOperator { get; set; }
    }

    private sealed class TestPrincipalHandler(
        IOptionsMonitor<TestPrincipalOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<TestPrincipalOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Options.IsOperator is not { } isOperator) return Task.FromResult(AuthenticateResult.NoResult());

            List<Claim> claims = isOperator
                ?
                [
                    new Claim(ClaimTypes.Name, "operator@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue),
                ]
                :
                [
                    new Claim(ClaimTypes.Name, "customer@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.TenantId, Guid.CreateVersion7().ToString()),
                ];

            var identity = new ClaimsIdentity(claims, TestScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
