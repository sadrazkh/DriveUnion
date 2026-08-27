using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Push;
using DriveUnion.Tests.Hosting;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// The notifications screen and its two endpoints, rendered and called by the real pipeline.
///
/// <para>A sibling of <c>PlanPageHarness</c> with one difference that is the whole point: it can be
/// built with or without VAPID keys in configuration. Both are states a deployment is really in —
/// no operator has set the keys on the day they install this — and the two produce completely
/// different screens, so a harness that could only make one of them would leave the other untested.
/// </para>
///
/// <para>The keys are generated per harness rather than checked in. A real key pair in a test file
/// is a real key pair in the repository, which is the one thing <c>VapidCredentials</c> exists to
/// keep out of it — and generating is one call.</para>
/// </summary>
public sealed class NotificationsPageHarness : WebApplicationFactory<Program>
{
    public const string TenantHeader = "X-Test-Tenant";

    public const string OperatorHeader = "X-Test-Operator";

    public const string UserName = "reza@acme.example";

    /// <summary>The person every request in this harness is signed in as. Rows are seeded against it.</summary>
    public static readonly Guid UserId = Guid.Parse("3c9f1a52-77d0-4e14-9a6b-2e7c58d31f04");

    private readonly SqliteConnection connection;

    private readonly bool configured;

    private readonly string publicKey;

    private readonly string privateKey;

    /// <param name="configured">
    /// Whether this deployment has VAPID keys. False is the ordinary state of a fresh installation,
    /// and the screen has to render it rather than offer a control that could only ever fail.
    /// </param>
    public NotificationsPageHarness(bool configured = true)
    {
        this.configured = configured;
        (publicKey, privateKey) = VapidCredentials.Generate();

        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();
        schema.Database.EnsureCreated();
    }

    /// <summary>The application server key this harness configured, for a test that checks the page prints it.</summary>
    public string PublicKey => publicKey;

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    public HttpClient NewClient(Guid? tenantId, bool asOperator = false, bool keepCookies = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = keepCookies,
        });

        if (tenantId is { } tenant) client.DefaultRequestHeaders.Add(TenantHeader, tenant.ToString());
        if (asOperator) client.DefaultRequestHeaders.Add(OperatorHeader, "yes");

        return client;
    }

    /// <summary>A workspace and the person the harness signs in as, both real rows.</summary>
    public Tenant SeedWorkspace(string name = "Acme")
    {
        var unique = Guid.NewGuid().ToString("N");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"t-{unique[..12]}",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var db = NewDbContext();
        db.Tenants.Add(tenant);

        db.Users.Add(new AppUser
        {
            Id = UserId,
            TenantId = tenant.Id,
            UserName = UserName,
            NormalizedUserName = UserName.ToUpperInvariant(),
            Email = UserName,
            NormalizedEmail = UserName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.SaveChanges();

        return tenant;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has read its own configuration.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("DriveUnion:PublicBaseUrl", "https://links.example.test");

        if (configured)
        {
            builder.UseSetting($"{VapidCredentials.SectionName}:{VapidCredentials.PublicKeyKey}", publicKey);
            builder.UseSetting($"{VapidCredentials.SectionName}:{VapidCredentials.PrivateKeyKey}", privateKey);
            builder.UseSetting($"{VapidCredentials.SectionName}:{VapidCredentials.SubjectKey}", "mailto:ops@example.test");
        }

        builder.ConfigureTestServices(services =>
        {
            // Before anything else, and before the connection below exists to be raced with. The
            // push worker is one of the loops this takes out — it opens a scope per event, and a
            // test that subscribes a device would otherwise have it reaching a real push service.
            services.RemoveEveryBackgroundLoop();

            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = HeaderAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = HeaderAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(
                    HeaderAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }

    /// <summary>The claims the cookie would carry, minted from headers instead.</summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "NotificationsHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenantHeader = Request.Headers[TenantHeader].ToString();
            var isOperator = Request.Headers.ContainsKey(OperatorHeader);
            var hasTenant = Guid.TryParse(tenantHeader, out var tenantId);

            if (!hasTenant && !isOperator) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, UserName),

                // NameIdentifier and not only the tenant: every route in this slice starts by asking
                // who is signed in, and a principal without it is one a real sign-in could never
                // produce — which would make the harness invent a 403 on a screen that works.
                new(ClaimTypes.NameIdentifier, UserId.ToString()),
            };

            if (hasTenant) claims.Add(new Claim(DriveUnionClaimTypes.TenantId, tenantId.ToString()));

            if (isOperator)
            {
                claims.Add(new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
