using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Seeding;

/// <summary>
/// The way out of the chicken-and-egg: there is no sign-up in M1 (tenant creation is M5), so a fresh
/// database has nobody who can sign in and no screen that would let anybody be created.
///
/// Idempotent by construction — every step looks for what it is about to create and leaves it alone
/// if it is already there. It never invents a password, never logs one, and never writes one back to
/// configuration; an operator email with no password beside it produces a warning and no account,
/// because a passwordless row would take the email and still refuse every sign-in.
/// </summary>
public sealed class IdentitySeeder(
    UserManager<AppUser> users,
    DriveUnionDbContext db,
    IOptions<DriveUnionSeedOptions> options,
    TimeProvider clock,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var seed = options.Value;

        var wantsOperator = !string.IsNullOrWhiteSpace(seed.OperatorEmail);
        var wantsTenant = !string.IsNullOrWhiteSpace(seed.TenantSlug);

        // Returning before any query matters: this runs at startup, and an unconfigured deployment
        // must not make the panel's boot depend on the database being reachable and migrated.
        if (!wantsOperator && !wantsTenant)
        {
            logger.LogDebug(
                "No {Section} configuration; nothing seeded.", DriveUnionSeedOptions.SectionName);
            return;
        }

        if (wantsOperator)
        {
            await SeedOperatorAsync(seed, cancellationToken).ConfigureAwait(false);
        }

        if (wantsTenant)
        {
            await SeedTenantAsync(seed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SeedOperatorAsync(DriveUnionSeedOptions seed, CancellationToken cancellationToken)
    {
        var email = seed.OperatorEmail!.Trim();

        if (await users.FindByEmailAsync(email).ConfigureAwait(false) is not null)
        {
            logger.LogInformation("Operator {Email} already exists; left untouched.", email);
            return;
        }

        if (string.IsNullOrEmpty(seed.OperatorPassword))
        {
            logger.LogWarning(
                "{Section}:OperatorEmail is set to {Email} but {Section}:OperatorPassword is not. "
                + "No account was created. Set it with user-secrets or the environment.",
                DriveUnionSeedOptions.SectionName,
                email,
                DriveUnionSeedOptions.SectionName);
            return;
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            // Nobody is going to click a confirmation mail for the account that exists so the panel
            // can be opened for the first time, and there is no mail sender in M1.
            EmailConfirmed = true,
            IsOperator = true,
            TenantId = null,
            DisplayName = seed.OperatorDisplayName,
            CreatedAt = clock.GetUtcNow(),
        };

        await CreateAsync(user, seed.OperatorPassword, "operator", cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedTenantAsync(DriveUnionSeedOptions seed, CancellationToken cancellationToken)
    {
        var slug = seed.TenantSlug!.Trim();

        var tenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(seed.TenantName) ? slug : seed.TenantName.Trim(),
                Slug = slug,
                CreatedAt = clock.GetUtcNow(),
            };

            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded tenant {Slug}.", slug);
        }
        else
        {
            // Deliberately not reconciled against TenantName. The slug is the folder name inside
            // every Drive account (DriveUnion/{slug}/), and a row that already exists may have files
            // under it; the seeder's job is to create a workspace, not to rename one.
            logger.LogInformation("Tenant {Slug} already exists; left untouched.", slug);
        }

        if (string.IsNullOrWhiteSpace(seed.TenantUserEmail))
        {
            return;
        }

        var email = seed.TenantUserEmail.Trim();

        if (await users.FindByEmailAsync(email).ConfigureAwait(false) is not null)
        {
            logger.LogInformation("Tenant user {Email} already exists; left untouched.", email);
            return;
        }

        if (string.IsNullOrEmpty(seed.TenantUserPassword))
        {
            logger.LogWarning(
                "{Section}:TenantUserEmail is set to {Email} but {Section}:TenantUserPassword is not. "
                + "No account was created.",
                DriveUnionSeedOptions.SectionName,
                email,
                DriveUnionSeedOptions.SectionName);
            return;
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            IsOperator = false,
            TenantId = tenant.Id,
            DisplayName = seed.TenantUserDisplayName,
            CreatedAt = clock.GetUtcNow(),
        };

        await CreateAsync(user, seed.TenantUserPassword, "tenant user", cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateAsync(
        AppUser user,
        string password,
        string what,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await users.CreateAsync(user, password).ConfigureAwait(false);

        if (result.Succeeded)
        {
            logger.LogInformation("Seeded {What} {Email}.", what, user.Email);
            return;
        }

        // Identity's descriptions say what is wrong with the password ("must be at least 10
        // characters"), never what it was. Reporting them is the only way the owner learns why the
        // account they configured is not there.
        logger.LogError(
            "Seeding {What} {Email} was refused: {Errors}",
            what,
            user.Email,
            string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
    }
}
