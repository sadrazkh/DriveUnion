using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// A real relational database for the service layer, in memory and gone at the end of the test.
///
/// SQLite rather than EF's in-memory provider because half of what this layer promises is SQL:
/// an atomic <c>DownloadCount + 1</c>, a unique index on the slug that a retry has to survive, a
/// transaction that keeps the counter and its audit row together. The in-memory provider has none
/// of those and would pass every one of these tests without testing anything.
///
/// The connection is opened here and held open for the harness's life: a SQLite <c>:memory:</c>
/// database is owned by its connection, and closing it between calls takes the schema with it.
/// </summary>
public sealed class ServiceTestHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly List<DriveUnionDbContext> _contexts = [];

    private ServiceTestHarness(SqliteConnection connection)
    {
        _connection = connection;
        Clock = new FixedClock(Now);
        Drive = new FakeDriveClient { Clock = Clock };
        Db = NewContext();
    }

    public FixedClock Clock { get; }

    public FakeDriveClient Drive { get; }

    /// <summary>The context the harness seeds through, and the one the services under test share.</summary>
    public DriveUnionDbContext Db { get; }

    public static ServiceTestHarness Create()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var harness = new ServiceTestHarness(connection);
        harness.Db.Database.EnsureCreated();

        return harness;
    }

    /// <summary>
    /// A second context over the same database, for the tests that need two callers holding their
    /// own snapshots of the same row.
    /// </summary>
    public DriveUnionDbContext NewContext()
    {
        var context = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>()
                .UseSqlite(_connection)
                .Options);

        _contexts.Add(context);

        return context;
    }

    public FileCatalog Files(DriveUnionDbContext? context = null) => new(context ?? Db, Clock);

    public ShareLinkService Links(ISlugGenerator? slugs = null, DriveUnionDbContext? context = null) =>
        new(context ?? Db, slugs ?? new SlugGenerator(), Clock);

    public PublicLinkReader PublicLinks(DriveUnionDbContext? context = null) => new(context ?? Db, Clock);

    public SingleAccountUploadTargetSelector Selector(DriveUnionDbContext? context = null) =>
        new(context ?? Db);

    /// <summary>
    /// The process-wide half of folder resolution, one per harness.
    ///
    /// <para>Every resolver and every coordinator this harness builds shares it, which is what makes
    /// «the second upload asks Drive nothing» a thing a test can assert. It is per harness and never
    /// static: a folder id cached across tests is a folder id from another test's fake Drive.</para>
    /// </summary>
    public DriveFolderCache FolderCache { get; } = new();

    /// <summary>
    /// The content keys of encrypted fetches in flight, one per harness.
    ///
    /// <para>Per harness and never static, for the reason FolderCache gives: a key left over from
    /// another test is a key for a fetch this one never made.</para>
    /// </summary>
    public FetchKeyring Keyring { get; } = new();

    public DriveFolders Folders(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Drive, FolderCache);

    /// <summary>
    /// The customer's folder tree — <c>Tree()</c> and not <c>Folders()</c>, which is taken above by
    /// the operator's Drive layout. Two different things called folders, and this is the one the
    /// customer names and arranges.
    /// </summary>
    public FolderTree Tree(DriveUnionDbContext? context = null) => new(context ?? Db, Clock);

    /// <summary>The operator's pool screen: what is on each account, and starting a drain.</summary>
    public AccountMigrations Migrations(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Clock);

    /// <summary>The queue behind «fetch this URL for me».</summary>
    public Infrastructure.Uploads.RemoteFetches Fetches(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Keyring, Clock);

    /// <summary>
    /// The half that pulls, pointed at a stub far end.
    ///
    /// <para>The handler is supplied rather than the guarded one, because what is under test here is
    /// how a response is read — not which addresses may be dialled, which is
    /// <c>RemoteAddressPolicyTests</c>' question and needs no network at all.</para>
    /// </summary>
    public Infrastructure.Uploads.RemoteFetcher Fetcher(
        HttpMessageHandler source,
        DriveUnionDbContext? context = null) =>
        new(
            context ?? Db,
            Uploads(context),
            new SingleClientFactory(source),
            Keyring,
            Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Uploads.RemoteFetcher>
                .Instance);

    /// <summary>
    /// The thing that actually moves a file, driven directly.
    ///
    /// <para>The hosted service around it is a <c>while</c> loop and a timer; everything worth
    /// asserting is here, which is exactly why the two are separate types.</para>
    /// </summary>
    public AccountMigrator Migrator(DriveUnionDbContext? context = null) =>
        new(
            context ?? Db,
            Drive,
            Folders(context ?? Db),
            Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountMigrator>.Instance);

    /// <summary>
    /// The workspace's labels — <c>Labels()</c> and not <c>Tags()</c>, so it reads as the thing on
    /// the screen rather than as the table underneath it.
    /// </summary>
    public TagStore Labels(DriveUnionDbContext? context = null) => new(context ?? Db, Clock);

    public UploadCoordinator Uploads(DriveUnionDbContext? context = null)
    {
        var db = context ?? Db;
        return new UploadCoordinator(
            db, Drive, Folders(db), new SingleAccountUploadTargetSelector(db), Clock);
    }

    public Tenant SeedTenant(string slug)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            CreatedAt = Now,
        };

        Db.Tenants.Add(tenant);
        Db.SaveChanges();

        return tenant;
    }

    public GoogleAccount SeedAccount(
        GoogleAccountStatus status = GoogleAccountStatus.Healthy,
        long quotaTotalBytes = 5L * 1024 * 1024 * 1024 * 1024,
        long quotaUsedBytes = 0)
    {
        var account = new GoogleAccount
        {
            Id = Guid.NewGuid(),
            Email = $"pool-{Guid.NewGuid():N}@example.com",
            Label = "A1",
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = quotaTotalBytes,
            QuotaUsedBytes = quotaUsedBytes,
            Status = status,
            CreatedAt = Now,
        };

        Db.GoogleAccounts.Add(account);
        Db.SaveChanges();

        return account;
    }

    /// <param name="content">
    /// The bytes, put into the fake Drive as well as the catalogue.
    ///
    /// <para>Null leaves a row with nothing behind it, which is the right fixture for everything that
    /// only reads the catalogue — and the wrong one for anything that moves or serves a file. A
    /// migration test seeded without this would «move» a file the fake never had.</para>
    /// </param>
    public StoredFile SeedFile(
        Guid tenantId,
        Guid accountId,
        string name = "quarterly.mp4",
        long sizeBytes = 1024,
        DateTimeOffset? deletedAt = null,
        byte[]? content = null,
        Guid? ownerUserId = null)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
            DriveFileId = $"drive-{Guid.NewGuid():N}",
            Name = name,
            MimeType = "video/mp4",
            SizeBytes = content?.LongLength ?? sizeBytes,
            CreatedAt = Now,
            ModifiedAt = Now,
            DeletedAt = deletedAt,
            OwnerUserId = ownerUserId,
        };

        Db.StoredFiles.Add(file);
        Db.SaveChanges();

        if (content is not null)
        {
            Drive.SeedFile(accountId, file.DriveFileId, name, file.MimeType, content);
        }

        return file;
    }

    /// <summary>Bytes a test can recognise again after they have been through a copy.</summary>
    public static byte[] Bytes(int length, byte seed = 7)
    {
        var content = new byte[length];
        for (var i = 0; i < length; i++) content[i] = (byte)((i * seed + 11) % 251);

        return content;
    }

    public ShareLink SeedLink(
        Guid tenantId,
        Guid storedFileId,
        string slug,
        DateTimeOffset? expiresAt = null,
        int? maxDownloads = null,
        int downloadCount = 0,
        bool isActive = true)
    {
        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            StoredFileId = storedFileId,
            TenantId = tenantId,
            ExpiresAt = expiresAt,
            MaxDownloads = maxDownloads,
            DownloadCount = downloadCount,
            IsActive = isActive,
            CreatedAt = Now,
        };

        Db.ShareLinks.Add(link);
        Db.SaveChanges();

        return link;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }
}

/// <summary>
/// An <see cref="IHttpClientFactory"/> that hands out one client over one handler.
///
/// <para>The real registration names its client and wires it to the guarded handler; a test needs to
/// point the fetcher at a stub far end instead, and this is the smallest thing that does it without
/// standing up the whole factory.</para>
/// </summary>
internal sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}
