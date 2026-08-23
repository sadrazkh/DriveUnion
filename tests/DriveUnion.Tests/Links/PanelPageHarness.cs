using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
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

namespace DriveUnion.Tests.Links;

/// <summary>
/// A panel page, rendered by the real pipeline, for a caller who is signed in.
///
/// The public routes have <c>PublicSiteHarness</c> and the sign-in page has
/// <c>IdentityPagesHarness</c>; neither can reach a page behind
/// <see cref="DriveUnionPolicies.Tenant"/>, so nothing in the suite had ever seen the panel shell
/// with a session in it. Cookie authentication is swapped for a header-driven scheme rather than
/// stood up for real, because what is under test is what the Razor renders for a tenant — not
/// Identity, which has its own tests.
///
/// A request without <see cref="TenantHeader"/> stays anonymous, so the same harness can ask what
/// an unauthenticated caller is answered with.
/// </summary>
public sealed class PanelPageHarness : WebApplicationFactory<Program>
{
    /// <summary>The tenant a request is signed in as. Absent means anonymous.</summary>
    public const string TenantHeader = "X-Test-Tenant";

    /// <summary>The signed-in display name, so the shell's footer has something to draw.</summary>
    public const string UserName = "reza@acme.example";

    private readonly SqliteConnection connection;

    public PanelPageHarness()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();

        // Includes DataProtectionKeys: the layout's antiforgery token is protected with that key
        // ring, and a missing table is a 500 on every page in the panel.
        schema.Database.EnsureCreated();
    }

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    /// <summary>Signed in as <paramref name="tenantId"/>, or anonymous when it is null.</summary>
    public HttpClient NewClient(Guid? tenantId)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        if (tenantId is { } tenant) client.DefaultRequestHeaders.Add(TenantHeader, tenant.ToString());

        return client;
    }

    /// <summary>A tenant with one file and one link over it. Returns the tenant.</summary>
    public Tenant SeedTenant(
        string name,
        string fileName,
        string slug,
        int? maxDownloads = null,
        int downloadCount = 0,
        bool isActive = true)
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
            Name = fileName,
            MimeType = "application/pdf",
            SizeBytes = 4096,
            CreatedAt = now,
            ModifiedAt = now,
        };

        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            MaxDownloads = maxDownloads,
            DownloadCount = downloadCount,
            IsActive = isActive,
            CreatedAt = now,
        };

        using var db = NewDbContext();
        db.Tenants.Add(tenant);
        db.GoogleAccounts.Add(account);
        db.StoredFiles.Add(file);
        db.ShareLinks.Add(link);
        db.SaveChanges();

        return tenant;
    }

    /// <summary>The pool account's address and the Drive file id — the two facts a customer must never see.</summary>
    public (string AccountEmail, string DriveFileId) SecretsOf(Guid tenantId)
    {
        using var db = NewDbContext();

        var file = db.StoredFiles.AsNoTracking().First(f => f.TenantId == tenantId);
        var email = db.GoogleAccounts.AsNoTracking().Single(a => a.Id == file.GoogleAccountId).Email;

        return (email, file.DriveFileId);
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
            // AddDbContext leaves the context, DbContextOptions, DbContextOptions<T> and EF 9's
            // IDbContextOptionsConfiguration<T> behind, and the last still carries UseNpgsql —
            // stacking UseSqlite on a surviving provider throws at resolve time.
            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

            // All three defaults, spelled out: AddIdentity has already pointed authenticate and
            // challenge at the application cookie, and setting only DefaultScheme would leave the
            // panel authenticating against a cookie no test can mint.
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TenantHeaderAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TenantHeaderAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TenantHeaderAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TenantHeaderAuthHandler>(
                    TenantHeaderAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }

    /// <summary>
    /// The claims the cookie would carry, minted from a header instead.
    ///
    /// Only the tenant claim, never the operator one: these tests are about what a customer sees,
    /// and the shell hides the whole Google pool behind exactly that distinction.
    /// </summary>
    private sealed class TenantHeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TenantHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers[TenantHeader].ToString();
            if (!Guid.TryParse(header, out var tenantId)) return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, UserName),
                    new Claim(DriveUnionClaimTypes.TenantId, tenantId.ToString()),
                ],
                SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
