using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Core.Metering;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Dashboard;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Hosting;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// <c>/</c>, rendered by the real pipeline — for a customer, for an operator, and for a session that
/// is neither.
///
/// <para>A sibling of <c>TrashPanelHarness</c>. It calls <c>AddDriveUnionDashboard()</c> — the same
/// one line <c>Program.cs</c> carries — rather than registering the two readers by hand, so what
/// passes here is what the application actually composes, and a registration that went missing from
/// the host would fail here too.</para>
///
/// <para><see cref="IDriveClient"/> becomes <see cref="FakeDriveClient"/>. Nothing on a dashboard
/// reaches Drive, and that is precisely why the substitution is here: a read that quietly acquired a
/// Google call would fail loudly rather than reach the network from a test.</para>
/// </summary>
public sealed class DashboardHarness : WebApplicationFactory<Program>
{
    /// <summary>The tenant a request is signed in as. Absent means anonymous.</summary>
    public const string TenantHeader = "X-Test-Tenant";

    /// <summary>Present means the principal also carries the operator claim.</summary>
    public const string OperatorHeader = "X-Test-Operator";

    public const string UserName = "reza@acme.example";

    public const long StorageQuotaBytes = 100L * 1024 * 1024 * 1024;

    public const long MonthlyEgressBytes = 500L * 1024 * 1024 * 1024;

    /// <summary>Five terabytes, which is what one of the operator's Google One accounts holds.</summary>
    public const long AccountTotalBytes = 5L * 1024 * 1024 * 1024 * 1024;

    private static readonly Guid SignedInUserId = Guid.Parse("8f3c1d64-2b7a-4d51-9a0e-6c8f2b17d4e3");

    private readonly SqliteConnection connection;

    public DashboardHarness()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();

        // EnsureCreated applies the model's seed data, which is where the plan catalogue and the
        // single OperatorSettings row come from.
        schema.Database.EnsureCreated();
    }

    public FakeDriveClient Drive { get; } = new();

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    public HttpClient NewClient(Guid? tenantId, bool asOperator = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        if (tenantId is { } tenant) client.DefaultRequestHeaders.Add(TenantHeader, tenant.ToString());
        if (asOperator) client.DefaultRequestHeaders.Add(OperatorHeader, "yes");

        return client;
    }

    /// <summary>
    /// A signed-in principal carrying no workspace and no operator claim — the account that exists
    /// before anybody has put it anywhere. The panel has to answer it, and «an empty dashboard» is
    /// not the answer.
    /// </summary>
    public HttpClient NewClientWithoutWorkspace()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        // A value that is deliberately not a Guid: the header is present, so the handler mints a
        // principal, and the tenant claim is absent because there is no tenant to name.
        client.DefaultRequestHeaders.Add(TenantHeader, "none");

        return client;
    }

    /// <summary>
    /// Egress already spent this month, as the roll-up row the meter would have written.
    ///
    /// <para>Written as a row rather than by calling <c>ITrafficMeter.RecordAsync</c>: what these
    /// screens are tested on is what they draw from the table, and going through the writer would
    /// mean a test of the dashboard that fails when the counter's arithmetic does. That arithmetic
    /// has <c>TrafficMeterTests</c>.</para>
    ///
    /// <para>Dated today, so it lands in the month the screen is about — and today in UTC, which is
    /// the clock <c>TrafficMeter</c> and everything else in this product stamps by.</para>
    /// </summary>
    public void SeedTrafficThisMonth(Guid tenantId, long bytes, int downloads = 1)
    {
        using var db = NewDbContext();

        db.TenantUsageDays.Add(new TenantUsageDay
        {
            TenantId = tenantId,
            Day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
            EgressBytes = bytes,
            Downloads = downloads,
        });

        db.SaveChanges();
    }

    public Tenant SeedWorkspace(string name, long storageUsedBytes = 0, long? storageQuotaBytes = null)
    {
        var now = DateTimeOffset.UtcNow;
        var unique = Guid.NewGuid().ToString("N");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"t-{unique[..12]}",
            CreatedAt = now,
            StorageQuotaBytes = storageQuotaBytes ?? StorageQuotaBytes,
            StorageUsedBytes = storageUsedBytes,
            MonthlyEgressBytes = MonthlyEgressBytes,
        };

        using var db = NewDbContext();
        db.Tenants.Add(tenant);
        db.SaveChanges();

        return tenant;
    }

    /// <summary>One Google account in the operator's pool. No tenant, ever — that is the product.</summary>
    public GoogleAccount SeedAccount(
        string label,
        string email,
        GoogleAccountStatus status = GoogleAccountStatus.Healthy,
        long usedBytes = 0,
        long totalBytes = AccountTotalBytes)
    {
        var account = new GoogleAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            Label = label,
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = totalBytes,
            QuotaUsedBytes = usedBytes,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var db = NewDbContext();
        db.GoogleAccounts.Add(account);
        db.SaveChanges();

        return account;
    }

    public StoredFile SeedFile(
        Tenant tenant,
        Guid accountId,
        string name,
        long sizeBytes,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deletedAt = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var moment = createdAt ?? DateTimeOffset.UtcNow;

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = accountId,
            DriveFileId = $"1{Guid.NewGuid():N}AbCdEf",
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = sizeBytes,
            CreatedAt = moment,
            ModifiedAt = moment,
            DeletedAt = deletedAt,
            DriveFolderId = deletedAt is null ? "folder-home" : "folder-trash",
        };

        using var db = NewDbContext();
        db.StoredFiles.Add(file);
        db.SaveChanges();

        return file;
    }

    public ShareLink SeedLink(
        Tenant tenant,
        Guid storedFileId,
        string slug,
        int downloadCount = 0,
        int? maxDownloads = null,
        bool isActive = true,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            StoredFileId = storedFileId,
            TenantId = tenant.Id,
            DownloadCount = downloadCount,
            MaxDownloads = maxDownloads,
            IsActive = isActive,
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var db = NewDbContext();
        db.ShareLinks.Add(link);
        db.SaveChanges();

        return link;
    }

    public UploadSession SeedSession(
        Tenant tenant,
        Guid accountId,
        string fileName,
        UploadSessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? failureReason = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = accountId,
            FileName = fileName,
            MimeType = "application/octet-stream",
            SizeBytes = 1024,
            DriveResumableUri = "https://upload.invalid/session",
            Status = status,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            FailureReason = failureReason,
        };

        using var db = NewDbContext();
        db.UploadSessions.Add(session);
        db.SaveChanges();

        return session;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has already read the connection string on its second line.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("DriveUnion:PublicBaseUrl", "https://links.example.test");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));

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

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IDriveClient)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IDriveClient>(Drive);

            // The one line Program.cs is missing. Written as the call the application will make, so
            // a registration that works here is a registration that works there.
            services.AddDriveUnionDashboard();

            // …and every background loop taken back out. Not only the purge sweeper this time:
            // <b>all</b> of them.
            //
            // Program.cs starts the trash sweeper, the Telegram drainer, its update poller and its
            // work-directory sweeper, and each opens its own scope on its own schedule against the
            // one SQLite connection this harness owns and disposes at the end of the test. That race
            // is real and it does not stay inside its own suite: run this file's thirty-odd host
            // tests alongside the rest and a NullReferenceException surfaces inside
            // SqliteConnection.Close() — sometimes here, more often in whichever other harness
            // happened to be tearing down at the same moment.
            //
            // What is under test here is what «/» draws. None of those loops contributes to it, so
            // none of them runs.
            services.RemoveEveryBackgroundLoop();

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
        public const string SchemeName = "DashboardHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenantHeader = Request.Headers[TenantHeader].ToString();
            var isOperator = Request.Headers.ContainsKey(OperatorHeader);
            var hasTenant = Guid.TryParse(tenantHeader, out var tenantId);

            // Neither header is a signed-in principal with no claims, which is a state the panel has
            // to answer for: an account created before it was given a workspace.
            var signedIn = Request.Headers.ContainsKey(TenantHeader)
                || Request.Headers.ContainsKey(OperatorHeader);

            if (!signedIn) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, UserName),
                new(ClaimTypes.NameIdentifier, SignedInUserId.ToString()),
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
