using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Hosting;
using DriveUnion.Web.Infrastructure;
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

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// The trash screen, the retention setting and the sidebar's capacity card, rendered by the real
/// pipeline — for a customer, for an operator, and for nobody.
///
/// <para>It is a sibling of <c>PlanPageHarness</c> and differs in what it registers. Three lines
/// stand in for the three <c>Program.cs</c> is missing:
/// <c>AddDriveUnionPlans</c> for the tenant's own numbers, <c>AddDriveUnionTrash</c> for the trash
/// and the operator settings row, and <c>AddDriveUnionTrashPanel</c> for the shell's capacity
/// reader. Written as those calls rather than as hand-rolled registrations, so what passes here is
/// what the application does once the lines land.</para>
///
/// <para><see cref="IDriveClient"/> becomes <see cref="FakeDriveClient"/>: emptying the trash
/// destroys bytes in Drive before it releases them, and no test may reach Google to do it.</para>
/// </summary>
public sealed class TrashPanelHarness : WebApplicationFactory<Program>
{
    /// <summary>The tenant a request is signed in as. Absent means anonymous.</summary>
    public const string TenantHeader = "X-Test-Tenant";

    /// <summary>Present means the principal also carries the operator claim.</summary>
    public const string OperatorHeader = "X-Test-Operator";

    public const string UserName = "reza@acme.example";

    /// <summary>
    /// The signed-in user's id, and it is a constant rather than a fresh Guid per request.
    ///
    /// <para>Antiforgery binds the token it issues to the identity of the caller it was issued to,
    /// so a handler that minted a new <see cref="ClaimTypes.NameIdentifier"/> on every request makes
    /// every token in the suite belong to somebody who no longer exists — and every POST fails with
    /// a 400 that looks exactly like a missing token.</para>
    /// </summary>
    public static readonly Guid SignedInUserId = Guid.Parse("2f9c1d64-3b7a-4d51-9a0e-6c8f2b17d4e3");

    /// <summary>A plan cap generous enough that no seeded workspace is accidentally over it.</summary>
    public const long StorageQuotaBytes = 100L * 1024 * 1024 * 1024;

    public const long MonthlyEgressBytes = 500L * 1024 * 1024 * 1024;

    private readonly SqliteConnection connection;

    public TrashPanelHarness()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();

        // EnsureCreated applies the model's seed data, which is where both the plan catalogue and
        // the single OperatorSettings row come from — so the retention screen reads the same seeded
        // 30 days a fresh deployment would.
        schema.Database.EnsureCreated();
    }

    /// <summary>The in-memory Drive every purge and every restore in these tests talks to.</summary>
    public FakeDriveClient Drive { get; } = new();

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

    /// <summary>A workspace and the pool account its files physically sit in.</summary>
    public Tenant SeedWorkspace(string name, long storageUsedBytes = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var unique = Guid.NewGuid().ToString("N");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"t-{unique[..12]}",
            CreatedAt = now,
            StorageQuotaBytes = StorageQuotaBytes,
            StorageUsedBytes = storageUsedBytes,
            MonthlyEgressBytes = MonthlyEgressBytes,
        };

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

        using var db = NewDbContext();
        db.Tenants.Add(tenant);
        db.GoogleAccounts.Add(account);
        db.SaveChanges();

        Accounts[tenant.Id] = account.Id;

        return tenant;
    }

    /// <summary>Which pool account a workspace's files were seeded into.</summary>
    public Dictionary<Guid, Guid> Accounts { get; } = [];

    /// <summary>
    /// A file already in the trash, with the folder it would go back to.
    /// </summary>
    /// <param name="purgeAfter">
    /// Null is a file deleted before the trash existed: it has no deadline, the sweeper leaves it
    /// alone, and only emptying the trash ever takes it.
    /// </param>
    public StoredFile SeedTrashedFile(
        Tenant tenant,
        string name,
        long sizeBytes,
        DateTimeOffset? purgeAfter,
        bool alsoInDrive = true)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var now = DateTimeOffset.UtcNow;
        var accountId = Accounts[tenant.Id];
        var driveFileId = $"1{Guid.NewGuid():N}AbCdEf";

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = accountId,
            DriveFileId = driveFileId,
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = sizeBytes,
            CreatedAt = now.AddDays(-3),
            ModifiedAt = now.AddDays(-3),
            DeletedAt = now.AddHours(-2),
            DriveFolderId = "folder-trash",
            RestoreFolderId = "folder-home",
            PurgeAfter = purgeAfter,
        };

        using var db = NewDbContext();
        db.StoredFiles.Add(file);
        db.SaveChanges();

        // The bytes as well as the row, because a restore is a move in Drive and the fake refuses to
        // move a file it has never heard of — which is exactly what Drive would do.
        if (alsoInDrive)
        {
            Drive.SeedFile(accountId, driveFileId, name, "application/pdf", new byte[Math.Min(sizeBytes, 32)]);
        }

        return file;
    }

    /// <summary>A live file, so a screen that must not show one has something to not show.</summary>
    public StoredFile SeedLiveFile(Tenant tenant, string name, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var now = DateTimeOffset.UtcNow;

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = Accounts[tenant.Id],
            DriveFileId = $"1{Guid.NewGuid():N}AbCdEf",
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = sizeBytes,
            CreatedAt = now,
            ModifiedAt = now,
            DriveFolderId = "folder-home",
        };

        using var db = NewDbContext();
        db.StoredFiles.Add(file);
        db.SaveChanges();

        return file;
    }

    /// <summary>The token a form on <paramref name="path"/> carries, fetched the way a browser gets it.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"{path} rendered no antiforgery token.");

        return match.Groups[1].Value;
    }

    /// <summary>A form post carrying the token the page issued, plus whatever fields are given.</summary>
    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string token,
        IDictionary<string, string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = token,
        };

        if (fields is not null)
        {
            foreach (var (key, value) in fields) form[key] = value;
        }

        return client.PostAsync(new Uri(path, UriKind.Relative), new FormUrlEncodedContent(form));
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

            // The three lines Program.cs is missing, in the order they depend on each other: the
            // capacity card reads a tenant's plan numbers and its trash, so both have to be there
            // before the panel's own registration is worth anything.
            services.AddDriveUnionPlans();
            services.AddDriveUnionTrash();
            services.AddDriveUnionTrashPanel();

            // …and the purge loop taken back out.
            //
            // Program.cs registers it, and it is a background service that opens its own scopes: in
            // this host that means a loop working against the one shared SQLite connection while a
            // request is mid-transaction, and calling into a FakeDriveClient whose own summary says
            // it is not thread-safe. Both produce failures that come and go with how busy the
            // machine is. What is under test here is what the screens do; the sweeper has its own
            // tests, and its own harness.
            // …and every other loop with it. Matching on the trash namespace left Program.cs's
            // Telegram drainer, poller and work sweeper running against this same connection, which
            // is the same defect wearing a different name.
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
        public const string SchemeName = "TrashHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tenantHeader = Request.Headers[TenantHeader].ToString();
            var isOperator = Request.Headers.ContainsKey(OperatorHeader);
            var hasTenant = Guid.TryParse(tenantHeader, out var tenantId);

            if (!hasTenant && !isOperator) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, UserName),

                // The retention setting records who changed it, and a principal with no usable id
                // would leave that column null in every test — including the one that is about the
                // column being written.
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
