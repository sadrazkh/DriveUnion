using System.Security.Claims;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// Identity over a real relational database, and the panel's real authorisation policies beside it.
///
/// The policies come from <see cref="DriveUnionWebServiceCollectionExtensions.AddDriveUnionWeb"/> —
/// the same call the web app makes — because a test that rebuilds the requirements by hand proves
/// that the test agrees with itself. What has to be true is that the claims this factory writes
/// satisfy the policies the panel is actually guarded by.
///
/// SQLite rather than EF's in-memory provider: the user store is queried for claims, roles and a
/// security stamp while a principal is being built, and the connection is held open for the
/// harness's life because a <c>:memory:</c> database dies with its connection.
/// </summary>
public sealed class IdentityTestHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;
    private readonly List<AsyncServiceScope> _extraScopes = [];

    private IdentityTestHarness(SqliteConnection connection, ServiceProvider root)
    {
        _connection = connection;
        _root = root;
        _scope = root.CreateAsyncScope();

        Users = _scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Db = _scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        Seeder = _scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
    }

    public UserManager<AppUser> Users { get; }

    public DriveUnionDbContext Db { get; }

    public IdentitySeeder Seeder { get; }

    public static IdentityTestHarness Create(params (string Key, string? Value)[] configuration)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

        // The same two options Program.cs sets, so a password the seeder is given here is judged by
        // the rule the deployment will judge it by.
        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DriveUnionDbContext>();

        services.AddDriveUnionIdentity(config);
        services.AddDriveUnionWeb();

        var harness = new IdentityTestHarness(connection, services.BuildServiceProvider());
        harness.Db.Database.EnsureCreated();

        return harness;
    }

    /// <summary>
    /// A row, with no password: nothing in these tests signs in, and a credential in a fixture is a
    /// credential that ends up quoted somewhere else.
    /// </summary>
    /// <param name="id">
    /// Its primary key, when the test is about which key it has — see the first-run tests, where the
    /// key is what two concurrent requests collide on.
    /// </param>
    public async Task<AppUser> AddUserAsync(
        string email,
        Guid? tenantId = null,
        bool isOperator = false,
        Guid? id = null)
    {
        var user = new AppUser
        {
            Id = id ?? Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            TenantId = tenantId,
            IsOperator = isOperator,
            CreatedAt = Now,
        };

        var result = await Users.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>
    /// A second request's worth of Identity: its own scope, and therefore its own change tracker
    /// over the same database.
    ///
    /// It matters for anything about two requests at once. EF refuses a second entity with a key it
    /// is already tracking, in memory, before a statement is sent — so a test that reuses
    /// <see cref="Users"/> proves the change tracker's rule and never reaches the database's. Two
    /// concurrent HTTP requests have two scoped contexts and neither knows what the other holds,
    /// which is the arrangement this reproduces.
    /// </summary>
    public UserManager<AppUser> SeparateScopeUsers()
    {
        var scope = _root.CreateAsyncScope();
        _extraScopes.Add(scope);

        return scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    }

    /// <summary>The principal the cookie would carry, built by the factory under test.</summary>
    public async Task<ClaimsPrincipal> PrincipalForAsync(AppUser user) =>
        await _scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>()
            .CreateAsync(user);

    public async Task<bool> SatisfiesAsync(string policyName, ClaimsPrincipal principal)
    {
        var policy = await _scope.ServiceProvider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(policyName)
            ?? throw new InvalidOperationException($"No policy named '{policyName}' is registered.");

        var result = await _scope.ServiceProvider
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, resource: null, policy);

        return result.Succeeded;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _extraScopes) await scope.DisposeAsync();

        await _scope.DisposeAsync();
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
