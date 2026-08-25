using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
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

    public DriveFolders Folders(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Drive, FolderCache);

    /// <summary>
    /// The customer's folder tree — <c>Tree()</c> and not <c>Folders()</c>, which is taken above by
    /// the operator's Drive layout. Two different things called folders, and this is the one the
    /// customer names and arranges.
    /// </summary>
    public FolderTree Tree(DriveUnionDbContext? context = null) => new(context ?? Db, Clock);

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

    public StoredFile SeedFile(
        Guid tenantId,
        Guid accountId,
        string name = "quarterly.mp4",
        long sizeBytes = 1024,
        DateTimeOffset? deletedAt = null)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
            DriveFileId = $"drive-{Guid.NewGuid():N}",
            Name = name,
            MimeType = "video/mp4",
            SizeBytes = sizeBytes,
            CreatedAt = Now,
            ModifiedAt = Now,
            DeletedAt = deletedAt,
        };

        Db.StoredFiles.Add(file);
        Db.SaveChanges();

        return file;
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
