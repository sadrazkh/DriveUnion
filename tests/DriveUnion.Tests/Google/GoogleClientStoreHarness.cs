using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// One database and as many stores over it as a test wants.
///
/// The second store is the point of the whole harness: the OAuth client used to be a JSON file
/// inside the container, and a redeploy deleted it while the accounts' refresh tokens — rows,
/// encrypted with a key ring that is also rows — survived and could no longer be refreshed. Building
/// a fresh store over the same database is the closest a test can get to that redeploy, and it has
/// to come back with the client intact.
///
/// SQLite stands in for Postgres. Nothing asserted through here is dialect-specific; the shared
/// cache is what lets the store's per-call scopes each open their own connection to the same data.
/// </summary>
internal sealed class GoogleClientStoreHarness : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;

    public GoogleClientStoreHarness()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>().Database.EnsureCreated();

        Protector = NewProtector();
    }

    /// <summary>
    /// The key ring every store built here shares by default — the arrangement the product has,
    /// where the keys live in the database and outlive the container.
    /// </summary>
    public ITokenProtector Protector { get; }

    public TimeProvider Clock { get; } = new ImmediateTimeProvider();

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }

    /// <summary>
    /// A store over this database. Called twice with no argument it is the panel before and after a
    /// restart; called with <see cref="NewProtector"/> it is a redeploy that lost its key ring.
    /// </summary>
    public GoogleOAuthClientStore Store(ITokenProtector? protector = null) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        protector ?? Protector,
        Clock,
        NullLogger<GoogleOAuthClientStore>.Instance);

    public GoogleOAuthClientImport Import(string path, ITokenProtector? protector = null)
    {
        var used = protector ?? Protector;

        return new GoogleOAuthClientImport(
            path,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            used,
            Store(used),
            NullLogger<GoogleOAuthClientImport>.Instance);
    }

    /// <summary>A context of this database's own, for a test that wants to read the raw rows.</summary>
    public T Read<T>(Func<DriveUnionDbContext, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        using var scope = _provider.CreateScope();

        return read(scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>());
    }

    public void Write(Action<DriveUnionDbContext> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        write(db);
        db.SaveChanges();
    }

    /// <summary>
    /// A fresh key ring. Two protectors from this method cannot read each other, which is exactly
    /// the redeploy the design put the real key ring in the database to avoid.
    /// </summary>
    public static ITokenProtector NewProtector() => new DataProtectionTokenProtector(
        new EphemeralDataProtectionProvider(),
        NullLogger<DataProtectionTokenProtector>.Instance);
}
