using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Seeding;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// The seeder is the only way an empty database gets an account that can sign in, and it runs on
/// every boot — so "does nothing" has to be its most reliable behaviour.
/// </summary>
public class IdentitySeederTests
{
    // Fixture values. The real ones are supplied through user-secrets or the environment and are
    // never in a file in this repository — which is the property the tests below are protecting.
    private const string OperatorEmail = "ops@example.com";
    private const string OperatorPassword = "Op3rator!Pass";
    private const string TenantUserEmail = "customer@example.com";
    private const string TenantUserPassword = "Cust0mer!Pass";

    [Fact]
    public async Task Seeds_nothing_when_nothing_is_configured()
    {
        await using var harness = IdentityTestHarness.Create();

        await harness.Seeder.SeedAsync();

        (await harness.Users.Users.CountAsync()).Should().Be(0);
        (await harness.Db.Tenants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Creates_the_operator_from_configuration()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", OperatorEmail),
            ($"{DriveUnionSeedOptions.SectionName}:OperatorPassword", OperatorPassword));

        await harness.Seeder.SeedAsync();

        var user = await harness.Users.FindByEmailAsync(OperatorEmail);

        user.Should().NotBeNull();
        user!.IsOperator.Should().BeTrue();

        // Operator staff have no tenant. A seeded one would hand the pool's owner a workspace they
        // never asked for and quietly put staff inside a customer's data.
        user.TenantId.Should().BeNull();
        (await harness.Users.CheckPasswordAsync(user, OperatorPassword)).Should().BeTrue();
    }

    [Fact]
    public async Task Running_twice_creates_one_account()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", OperatorEmail),
            ($"{DriveUnionSeedOptions.SectionName}:OperatorPassword", OperatorPassword));

        await harness.Seeder.SeedAsync();
        await harness.Seeder.SeedAsync();

        (await harness.Users.Users.CountAsync(u => u.Email == OperatorEmail)).Should().Be(1);
    }

    [Fact]
    public async Task An_existing_account_is_not_rewritten()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", OperatorEmail),
            ($"{DriveUnionSeedOptions.SectionName}:OperatorPassword", OperatorPassword));

        // Somebody changed their own password, or was demoted. A seeder that "corrects" the row on
        // the next deploy silently hands the account back its old credential.
        var existing = await harness.AddUserAsync(OperatorEmail, isOperator: false);

        await harness.Seeder.SeedAsync();

        var user = await harness.Users.FindByIdAsync(existing.Id.ToString());

        user.Should().NotBeNull();
        user!.IsOperator.Should().BeFalse();
        (await harness.Users.HasPasswordAsync(user)).Should().BeFalse();
    }

    [Fact]
    public async Task An_email_with_no_password_creates_nothing()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", OperatorEmail));

        await harness.Seeder.SeedAsync();

        // Not a passwordless row either: it would take the address and still refuse every sign-in,
        // and the next boot with a password configured would find it and leave it exactly as it is.
        (await harness.Users.Users.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_password_the_policy_refuses_creates_nothing()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", OperatorEmail),
            ($"{DriveUnionSeedOptions.SectionName}:OperatorPassword", "short"));

        await harness.Seeder.SeedAsync();

        (await harness.Users.Users.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seeds_a_development_tenant_and_a_user_inside_it()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:TenantSlug", "acme"),
            ($"{DriveUnionSeedOptions.SectionName}:TenantName", "Acme Co"),
            ($"{DriveUnionSeedOptions.SectionName}:TenantUserEmail", TenantUserEmail),
            ($"{DriveUnionSeedOptions.SectionName}:TenantUserPassword", TenantUserPassword));

        await harness.Seeder.SeedAsync();

        var tenant = await harness.Db.Tenants.SingleAsync(t => t.Slug == "acme");
        tenant.Name.Should().Be("Acme Co");

        var user = await harness.Users.FindByEmailAsync(TenantUserEmail);

        user.Should().NotBeNull();
        user!.IsOperator.Should().BeFalse();
        user.TenantId.Should().Be(tenant.Id);
    }

    [Fact]
    public async Task An_existing_tenant_keeps_its_name_and_its_id()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:TenantSlug", "acme"),
            ($"{DriveUnionSeedOptions.SectionName}:TenantName", "Renamed"));

        harness.Db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme Co",
            Slug = "acme",
            CreatedAt = IdentityTestHarness.Now,
        });
        await harness.Db.SaveChangesAsync();

        await harness.Seeder.SeedAsync();

        // The slug names the per-tenant folder inside every Drive account, and the id is on every
        // file row. Reconciling either from configuration on a later boot orphans what is stored.
        var tenant = await harness.Db.Tenants.SingleAsync(t => t.Slug == "acme");
        tenant.Name.Should().Be("Acme Co");
    }
}
