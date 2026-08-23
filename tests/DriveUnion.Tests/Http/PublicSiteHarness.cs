using System.Net;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Http;

/// <summary>
/// One seeded link, and everything a test needs to talk about it — including the two facts that
/// must never reach a visitor: the Drive file id and the pool account's email address.
/// </summary>
public sealed record SeededLink(
    Guid LinkId,
    Guid TenantId,
    string Slug,
    string FileName,
    string DriveFileId,
    string GoogleAccountEmail,
    byte[] Content);

/// <summary>
/// DriveUnion.Web's real pipeline, in this process, with exactly two things swapped out.
///
/// <list type="number">
/// <item>
/// The Npgsql <see cref="DriveUnionDbContext"/> becomes SQLite in memory. SQLite rather than EF's
/// in-memory provider because the download path is SQL: <c>ExecuteUpdateAsync</c> computing
/// <c>DownloadCount + 1</c> inside a transaction, and a unique index on the slug.
/// </item>
/// <item>
/// <see cref="IDriveClient"/> becomes <see cref="FakeDriveClient"/>, so no test reaches Google.
/// </item>
/// </list>
///
/// Everything else is the genuine article: routing, the rate limiter and its real numbers, the real
/// <c>PublicDownloadController</c>, the real <c>PublicLinkReader</c>, the real Razor views. That is
/// the whole point — the failure this suite exists to catch (spec §8) is a tenant filter turning an
/// anonymous request into <c>Guid.Empty</c>, and no unit test can see it because the failure IS the
/// absence of a session.
///
/// One harness per test, on purpose. The rate limiter partitions on the connection's address, which
/// TestServer leaves null, so every request in a container shares one bucket — a shared harness
/// would let one test spend another's permits.
/// </summary>
public sealed class PublicSiteHarness : WebApplicationFactory<Program>
{
    /// <summary>Configured origin for the copyable link. Deliberately not a Google address.</summary>
    public const string PublicBaseUrl = "https://links.example.test";

    private readonly SqliteConnection connection;

    public PublicSiteHarness()
    {
        // A SQLite :memory: database belongs to its connection. Held open for the harness's life,
        // or the schema — and every seeded row — vanishes between two requests of the same test.
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = NewDbContext();
        schema.Database.EnsureCreated();

        DriveClient = Drive;
    }

    /// <summary>The in-memory Drive. Seed it, then read <see cref="FakeDriveClient.Calls"/>.</summary>
    public FakeDriveClient Drive { get; } = new();

    /// <summary>
    /// What the pipeline resolves for <see cref="IDriveClient"/>. Assign before the first request to
    /// wrap <see cref="Drive"/> — the mid-download failure test needs a body that dies after a few
    /// bytes, which the fake alone cannot produce.
    /// </summary>
    public IDriveClient DriveClient { get; set; }

    /// <summary>
    /// Opt in to <see cref="TestRemoteAddressHeader"/>, a third substitution used by exactly one
    /// test.
    ///
    /// The limiter partitions on the connection's address, which TestServer always leaves null — so
    /// by default every request in this harness shares one bucket. That is what makes the burst
    /// tests deterministic, and it is also why nothing else can see whether the partitioning works
    /// at all. Off unless a test asks for it.
    /// </summary>
    public bool PartitionByTestRemoteAddress { get; init; }

    /// <summary>Read only when <see cref="PartitionByTestRemoteAddress"/> is set.</summary>
    public const string TestRemoteAddressHeader = "X-Test-Remote-Ip";

    /// <summary>
    /// A client that keeps no cookie jar and follows no redirect.
    ///
    /// No cookie jar because the product's central claim is that a stranger with a link needs no
    /// account; a handler that quietly carried a session cookie between two requests would hide the
    /// very thing under test. No redirects because "never bounce the visitor to drive.google.com"
    /// is only observable if the 302 is left unfollowed.
    /// </summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    /// <summary>Deterministic bytes, so a mangled body is visible at the first differing index.</summary>
    public static byte[] TestBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);

        return bytes;
    }

    /// <summary>
    /// A tenant, a pool account, a stored file, its bytes in the fake Drive, and a share link —
    /// written straight through the DbContext, because the panel's own API is not what is under test.
    /// </summary>
    public SeededLink SeedLink(
        string slug,
        byte[]? content = null,
        string fileName = "quarterly-report.mp4",
        string mimeType = "video/mp4",
        DateTimeOffset? expiresAt = null,
        int? maxDownloads = null,
        int downloadCount = 0,
        bool isActive = true)
    {
        var bytes = content ?? TestBytes(4096);
        var now = DateTimeOffset.UtcNow;
        var unique = Guid.NewGuid().ToString("N");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = $"t-{unique[..12]}",
            CreatedAt = now,
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

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleAccountId = account.Id,
            DriveFileId = $"1{unique}AbCdEf",
            Name = fileName,
            MimeType = mimeType,
            SizeBytes = bytes.LongLength,
            CreatedAt = now,
            ModifiedAt = now,
        };

        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            ExpiresAt = expiresAt,
            MaxDownloads = maxDownloads,
            DownloadCount = downloadCount,
            IsActive = isActive,
            CreatedAt = now,
        };

        using (var db = NewDbContext())
        {
            db.Tenants.Add(tenant);
            db.GoogleAccounts.Add(account);
            db.StoredFiles.Add(file);
            db.ShareLinks.Add(link);
            db.SaveChanges();
        }

        Drive.SeedFile(account.Id, file.DriveFileId, fileName, mimeType, bytes);

        return new SeededLink(link.Id, tenant.Id, slug, fileName, file.DriveFileId, account.Email, bytes);
    }

    /// <summary>The denormalised counter, read back through the database rather than trusted.</summary>
    public async Task<int> DownloadCountAsync(Guid linkId)
    {
        await using var db = NewDbContext();

        return await db.ShareLinks.AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => l.DownloadCount)
            .SingleAsync();
    }

    public async Task<int> DownloadEventCountAsync(Guid linkId)
    {
        await using var db = NewDbContext();

        return await db.DownloadEvents.AsNoTracking().CountAsync(d => d.ShareLinkId == linkId);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production, because that is the pipeline a visitor meets: the exception handler and HSTS
        // are part of what /d/{slug} answers with, and a Development-only page would hide a 500.
        builder.UseEnvironment("Production");

        // UseSetting and not ConfigureAppConfiguration: under the minimal hosting model the latter
        // is applied while the host is being built, and Program.cs reads the connection string on
        // its second line — long before that. Host settings reach the app's configuration first.
        //
        // The connection string itself is never dialled; Program.cs simply refuses to start without
        // one, because the panel holds encrypted Google credentials and must not fall back to an
        // implicit store. The registration it produces is replaced with SQLite below.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("DriveUnion:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("DriveUnion:DownloadIpHashKey", "drive-union-integration-test-key");

        builder.ConfigureTestServices(services =>
        {
            ReplaceNpgsqlWithSqlite(services);

            RemoveAllOf(services, typeof(IDriveClient));

            // Resolved through the property so a test can wrap the fake after construction but
            // before the first request.
            services.AddSingleton(_ => DriveClient);

            if (PartitionByTestRemoteAddress)
            {
                services.AddSingleton<IStartupFilter, TestRemoteAddressStartupFilter>();
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }

    private void ReplaceNpgsqlWithSqlite(IServiceCollection services)
    {
        // AddDbContext leaves four kinds of registration behind: the context, DbContextOptions,
        // DbContextOptions<T>, and — since EF Core 9 — an IDbContextOptionsConfiguration<T> that
        // still carries UseNpgsql. Adding UseSqlite on top of a surviving provider registration
        // throws at resolve time, so everything naming this context goes first. Matching on the
        // generic argument catches the configuration type without naming it, which keeps this
        // working across EF's own churn in that area.
        var doomed = services
            .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType
                    && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
            .ToList();

        foreach (var descriptor in doomed) services.Remove(descriptor);

        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));
    }

    private static void RemoveAllOf(IServiceCollection services, Type serviceType)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == serviceType).ToList())
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    /// Gives the request the address a header names, ahead of everything Program.cs registers.
    ///
    /// An <see cref="IStartupFilter"/> rather than a header the pipeline already honours, because
    /// <c>UseForwardedHeaders</c> correctly refuses to believe an <c>X-Forwarded-For</c> from an
    /// untrusted peer — and under TestServer the peer has no address at all, so it is never trusted.
    /// This stands in for the OVH proxy having already done that job.
    /// </summary>
    private sealed class TestRemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                var header = context.Request.Headers[TestRemoteAddressHeader].ToString();
                if (IPAddress.TryParse(header, out var address)) context.Connection.RemoteIpAddress = address;

                await following(context);
            });

            next(app);
        };
    }
}
