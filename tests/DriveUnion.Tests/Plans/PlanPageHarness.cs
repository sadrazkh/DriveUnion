using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
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

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The plan screens, rendered by the real pipeline, for a customer, for an operator, and for nobody.
///
/// <para>It is a sibling of <c>PanelPageHarness</c> and differs in two ways that matter: it can mint
/// an operator principal, because half of what P1 draws is operator-only and a customer reaching it
/// is the failure worth proving impossible; and it calls
/// <see cref="PlanServiceCollectionExtensions.AddDriveUnionPlans"/>, which is the one line
/// <c>Program.cs</c> needs and which P1 was not allowed to add there. What the tests below prove is
/// therefore exactly what that line does.</para>
/// </summary>
public sealed class PlanPageHarness : WebApplicationFactory<Program>
{
    /// <summary>The tenant a request is signed in as. Absent means anonymous.</summary>
    public const string TenantHeader = "X-Test-Tenant";

    /// <summary>Present means the principal also carries the operator claim.</summary>
    public const string OperatorHeader = "X-Test-Operator";

    public const string UserName = "reza@acme.example";

    /// <summary>
    /// Who the signed-in caller is, as a claim rather than only as a display name.
    ///
    /// <para>A cookie minted by ASP.NET Identity always carries <c>NameIdentifier</c>; this handler
    /// minted <c>Name</c> and the tenant and stopped, so its principal was one a real sign-in could
    /// never produce. Nothing noticed until a test pressed every link in the sidebar: /telegram/link
    /// is a tenant route that starts <c>if (CurrentUserId() is not { } userId) return Forbid()</c>,
    /// so it answered 403 to a customer the harness said was signed in — a refusal invented by the
    /// harness, on a screen that works.</para>
    ///
    /// <para>Fixed, not exempted. A test that skips the routes a harness cannot reach is a test that
    /// stops covering them the moment the harness is the thing that is wrong.</para>
    /// </summary>
    public static readonly Guid UserId = Guid.Parse("9f2a6c14-0d3b-4c8e-9a55-6b1f2c7d4e80");

    private readonly SqliteConnection connection;

    public PlanPageHarness()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();

        // EnsureCreated applies the model's seed data, so the plan catalogue is there — which is
        // also what proves the catalogue is seeded by the model rather than by a start-up hook
        // nothing in a test host runs.
        schema.Database.EnsureCreated();
    }

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    /// <summary>
    /// Signed in as <paramref name="tenantId"/>, or anonymous when it is null.
    /// </summary>
    /// <param name="keepCookies">
    /// Needed only by a test that posts: the antiforgery token in the page is half of a pair, and
    /// without its cookie the POST is refused for the wrong reason.
    /// </param>
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

    /// <summary>A workspace with one file and one live link over it.</summary>
    public (Tenant Tenant, ShareLink Link, StoredFile File) SeedWorkspace(string name, long fileBytes = 4096)
    {
        var now = DateTimeOffset.UtcNow;
        var unique = Guid.NewGuid().ToString("N");

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = name, Slug = $"t-{unique[..12]}", CreatedAt = now };

        var account = new GoogleAccount
        {
            Id = Guid.NewGuid(),
            Email = $"pool-{unique}@gmail.com",
            Label = "A1",
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = 5L * 1024 * 1024 * 1024 * 1024,
            QuotaUsedBytes = 0,
            Status = GoogleAccountStatus.Healthy,
            CreatedAt = now,
        };

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = account.Id,
            DriveFileId = $"1{unique}AbCdEf",
            Name = "quarterly-report.pdf",
            MimeType = "application/pdf",
            SizeBytes = fileBytes,
            CreatedAt = now,
            ModifiedAt = now,
        };

        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Slug = SlugFor(unique),
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            DownloadCount = 0,
            IsActive = true,
            CreatedAt = now,
        };

        using var db = NewDbContext();
        db.Tenants.Add(tenant);
        db.GoogleAccounts.Add(account);
        db.StoredFiles.Add(file);
        db.ShareLinks.Add(link);
        db.SaveChanges();

        return (tenant, link, file);
    }

    /// <summary>The plan service over this harness's database, for arranging a downgrade.</summary>
    public TenantPlanService Plans() =>
        new(NewDbContext(), TimeProvider.System, Options.Create(new PlansOptions()));

    /// <summary>
    /// A slug the public reader will accept. It has to be well formed or the lookup answers without
    /// a query at all, which would make a "the link still works" assertion pass for the wrong reason.
    /// </summary>
    private static string SlugFor(string unique)
    {
        var slug = new string([.. unique.Where(char.IsLetterOrDigit).Take(SlugGenerator.SlugLength)]);

        return SlugGenerator.IsWellFormed(slug)
            ? slug
            : throw new InvalidOperationException($"'{slug}' is not a slug this product would mint.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has already read the connection string on its second line.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("DriveUnion:PublicBaseUrl", "https://links.example.test");

        builder.ConfigureTestServices(services =>
        {
            // Before anything else, and before the connection below exists to be raced with.
            services.RemoveEveryBackgroundLoop();

            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

            // The one line Program.cs is missing. Written as the same call rather than as three
            // hand-rolled registrations, so a test that passes here is a test that passes there.
            services.AddDriveUnionPlans();

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
        public const string SchemeName = "PlanHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenantHeader = Request.Headers[TenantHeader].ToString();
            var isOperator = Request.Headers.ContainsKey(OperatorHeader);
            var hasTenant = Guid.TryParse(tenantHeader, out var tenantId);

            if (!hasTenant && !isOperator) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, UserName),
                new(ClaimTypes.NameIdentifier, UserId.ToString()),
            };

            if (hasTenant) claims.Add(new Claim(DriveUnionClaimTypes.TenantId, tenantId.ToString()));

            if (isOperator)
            {
                claims.Add(new Claim(
                    DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
