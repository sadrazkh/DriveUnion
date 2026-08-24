using DriveUnion.Core.Application;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Tests.Fakes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// Provisioning over a real relational database, in memory and gone at the end of the test.
///
/// <para>SQLite rather than EF's in-memory provider, for the same reason the service lane already
/// uses it: half of what this layer promises is SQL. The unique index on <c>Tenant.Slug</c> that
/// decides a race, the transaction that rolls a workspace back when its plan will not apply, and
/// Identity's own user store are none of them exercised by a provider that keeps rows in a
/// dictionary.</para>
///
/// <para>The registrations are the product's own extension methods — <c>AddDriveUnionPlans</c> and
/// <c>AddDriveUnionTenancy</c> — rather than three hand-rolled lines, so a test that passes here is
/// a test that passes against the container the panel builds.</para>
/// </summary>
public sealed class TenantServiceHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Comfortably over Identity's ten-character minimum, and not a real credential.</summary>
    public const string Password = "Correct-Horse-9!";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    private TenantServiceHarness(SqliteConnection connection, ServiceProvider root)
    {
        _connection = connection;
        _root = root;
        _scope = root.CreateAsyncScope();

        Db = _scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        Users = _scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Provisioning = _scope.ServiceProvider.GetRequiredService<ITenantProvisioning>();
        Directory = _scope.ServiceProvider.GetRequiredService<IOperatorTenantDirectory>();
        Plans = _scope.ServiceProvider.GetRequiredService<ITenantPlanService>();
    }

    public DriveUnionDbContext Db { get; }

    public UserManager<AppUser> Users { get; }

    public ITenantProvisioning Provisioning { get; }

    public IOperatorTenantDirectory Directory { get; }

    public ITenantPlanService Plans { get; }

    public static TenantServiceHarness Create()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

        // The password reset path mints a Data Protection token, so the key ring has to exist. In
        // memory here: the panel persists it to the database, and none of that is what is under test.
        services.AddDataProtection();

        // The same two options Program.cs sets, so a password judged here is judged by the rule the
        // deployment will judge it by.
        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DriveUnionDbContext>()
            .AddDefaultTokenProviders();

        services.AddDriveUnionIdentity(configuration);
        services.AddDriveUnionPlans();
        services.AddDriveUnionTenancy();

        var harness = new TenantServiceHarness(connection, services.BuildServiceProvider());

        // EnsureCreated applies the model's seed data, so the plan catalogue is there — which is
        // also what a new workspace's default plan is looked up in.
        harness.Db.Database.EnsureCreated();

        return harness;
    }

    /// <summary>A workspace put there directly, for the tests that are about what happens next.</summary>
    public Tenant SeedTenant(string slug, int maxMembers = 3)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            CreatedAt = Now,
            MaxMembers = maxMembers,
        };

        Db.Tenants.Add(tenant);
        Db.SaveChanges();

        return tenant;
    }

    public int MemberCount(Guid tenantId) => Db.Users.Count(u => u.TenantId == tenantId);

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
