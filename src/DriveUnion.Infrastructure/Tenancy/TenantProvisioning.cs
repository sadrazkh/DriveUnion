using DriveUnion.Core.Application;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Tenancy;

/// <summary>
/// Creating a workspace, creating the people in it, and taking their access away again.
///
/// <para>This is the thing the product did not have. Until it existed the only way to onboard a
/// customer was to set four environment variables and redeploy, because
/// <c>Infrastructure/Seeding</c> held every <c>new Tenant</c> and every <c>CreateAsync</c> in
/// <c>src/</c>. The seeder is still how an empty database gets its first operator; this is
/// everything after that.</para>
///
/// <para><b>Every command takes the tenant explicitly and scopes on it.</b> A member is found by
/// <c>WHERE Id = @userId AND TenantId = @tenantId</c>, never by id alone. That is not belt and
/// braces: an operator's own account has a null <c>TenantId</c>, so the same clause that stops a
/// mistyped id reaching another customer's member also stops this screen ever locking an operator
/// out of the panel it lives in.</para>
/// </summary>
public sealed class TenantProvisioning(
    DriveUnionDbContext db,
    UserManager<AppUser> users,
    ITenantPlanService plans,
    IPlanCatalogueReader catalogue,
    TimeProvider clock,
    ILogger<TenantProvisioning> logger) : ITenantProvisioning
{
    /// <summary>
    /// What the quota history records for the first plan a workspace is given. It names the act
    /// rather than the operator, because the operator's id is already on the row.
    /// </summary>
    private const string CreationReason = "Applied when the workspace was created.";

    public async Task<TenantProvisioningResult> CreateTenantAsync(
        string name,
        string slug,
        string? planCode,
        Guid? createdByUserId,
        CancellationToken cancellationToken)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > 200)
        {
            return TenantProvisioningResult.Refused(TenantRefusal.NameRequired);
        }

        var normalisedSlug = TenantSlug.Normalise(slug);
        if (!TenantSlug.IsWellFormed(normalisedSlug))
        {
            return TenantProvisioningResult.Refused(TenantRefusal.SlugMalformed);
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == normalisedSlug, cancellationToken))
        {
            return TenantProvisioningResult.Refused(TenantRefusal.SlugTaken);
        }

        // The plan is resolved before the workspace is written, so a mistyped tier costs nothing.
        // Retired tiers are hidden from new assignment — a workspace already on one keeps working,
        // which is the whole of what retirement means — and the service would refuse anyway; asking
        // here is what turns that refusal into a sentence instead of an exception.
        var wanted = string.IsNullOrWhiteSpace(planCode) ? null : planCode.Trim();
        if (wanted is not null)
        {
            var tier = await catalogue.FindAsync(wanted, cancellationToken);
            if (tier is null || tier.IsRetired)
            {
                return TenantProvisioningResult.Refused(TenantRefusal.PlanNotFound);
            }
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Slug = normalisedSlug,
            CreatedAt = clock.GetUtcNow(),
        };

        // One transaction over both writes. A workspace whose plan did not apply carries the column
        // defaults, which is a working configuration that nobody chose — and "nobody chose it" is
        // exactly the state this screen exists to end. Rolling the workspace back leaves the
        // operator with a form to correct rather than a row to explain.
        var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        try
        {
            db.Tenants.Add(tenant);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException taken)
            {
                // The unique index on Slug decided it, which is the only arbiter that survives two
                // operators submitting the same slug in the same instant — the check above closes
                // the ordinary case and this closes the window inside it.
                logger.LogInformation(taken, "Slug {Slug} was taken while the workspace was created.", normalisedSlug);

                db.Entry(tenant).State = EntityState.Detached;

                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);

                return TenantProvisioningResult.Refused(TenantRefusal.SlugTaken);
            }

            var applied = wanted is null
                ? await plans.ApplyDefaultPlanAsync(tenant.Id, createdByUserId, cancellationToken)
                : await plans.SetTenantPlanAsync(
                    tenant.Id, wanted, CreationReason, createdByUserId, cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Created workspace {Slug} on plan {Plan}.", tenant.Slug, applied.PlanCode);

            return new TenantProvisioningResult(
                TenantRefusal.None,
                new TenantProvisioned(tenant.Id, tenant.Name, tenant.Slug, applied.Limits));
        }
        catch (KeyNotFoundException missing)
        {
            // Plans:DefaultPlanCode names a tier that is not in the catalogue, or the tier was
            // deleted between the lookup above and the apply. Either way nothing is left behind.
            logger.LogError(missing, "No plan to give the new workspace {Slug}.", normalisedSlug);

            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);

            return TenantProvisioningResult.Refused(TenantRefusal.PlanNotFound);
        }
        catch (InvalidOperationException retired)
        {
            logger.LogError(retired, "The plan for the new workspace {Slug} was refused.", normalisedSlug);

            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);

            return TenantProvisioningResult.Refused(TenantRefusal.PlanNotFound);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<MemberProvisioningResult> CreateMemberAsync(
        Guid tenantId,
        string email,
        string? displayName,
        string password,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.MaxMembers })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null) return MemberProvisioningResult.Refused(MemberRefusal.TenantNotFound);

        var address = email?.Trim() ?? string.Empty;
        if (address.Length == 0) return MemberProvisioningResult.Refused(MemberRefusal.EmailRequired);

        var seatsUsed = await db.Users.CountAsync(u => u.TenantId == tenantId, cancellationToken);

        // Before the account exists, not after. An account created and then apologised for is an
        // account that can sign in, and refusing at the cap is the entire reason MaxMembers is a
        // column rather than a note in a contract.
        //
        // Two operators submitting the last seat in the same instant can both pass this count: the
        // airtight version is the conditional UPDATE the storage reservation uses, which needs a
        // seat counter on the row and therefore a migration. Recorded rather than hidden — the
        // overshoot is one seat, by one human's console, and the cap is re-read on every screen.
        if (seatsUsed >= tenant.MaxMembers)
        {
            return new MemberProvisioningResult(
                MemberRefusal.SeatsFull, null, [], seatsUsed, tenant.MaxMembers);
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = address,
            Email = address,

            // Nobody is going to click a confirmation mail for an account whose address and password
            // were both typed by the operator, and there is no mail sender in this product. Same
            // reasoning as IdentitySeeder's, and the same as the first-run screen's.
            EmailConfirmed = true,

            // Hard-coded, in the one place outside the config seeder that creates an account. M5 §8
            // leaves the seeder as the only writer of this flag in the codebase, and the request
            // shapes this method is called with carry no field that could reach it.
            IsOperator = false,
            TenantId = tenantId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),

            // Set here rather than left to Identity's AllowedForNewUsers default: disabling somebody
            // is a lockout, and a row created with lockout disabled is a row this panel's disable
            // button silently does nothing to.
            LockoutEnabled = true,
            CreatedAt = clock.GetUtcNow(),
        };

        var result = await users.CreateAsync(user, password ?? string.Empty);

        if (!result.Succeeded)
        {
            // Identity's own descriptions, already in the panel's language through
            // DriveUnionIdentityErrorDescriber. They say what is wrong with the password and never
            // what it was.
            return new MemberProvisioningResult(
                MemberRefusal.IdentityRefused,
                null,
                [.. result.Errors.Select(e => e.Description)],
                seatsUsed,
                tenant.MaxMembers);
        }

        logger.LogInformation("Created {Email} in workspace {TenantId}.", address, tenantId);

        return new MemberProvisioningResult(
            MemberRefusal.None, user.Id, [], seatsUsed + 1, tenant.MaxMembers);
    }

    /// <summary>
    /// Disabling, and <b>why it takes effect on the next request rather than the next sign-in.</b>
    ///
    /// <para>Two things are needed and neither is enough alone. The lockout closes the door: Identity
    /// refuses <c>PasswordSignInAsync</c> for a user whose <c>LockoutEnd</c> is in the future, so
    /// nobody signs in again. But a person being disabled is usually a person who is signed in right
    /// now, and their cookie is a self-contained credential that the server does not consult the
    /// database about — it would keep working until it expired.</para>
    ///
    /// <para>So the security stamp is bumped as well. The cookie carries the stamp it was minted
    /// with, <c>SecurityStampValidator</c> compares it against the row on the way in, and a mismatch
    /// rejects the principal and signs the session out. <c>AddDriveUnionTenancy</c> sets that
    /// validator's interval to zero, so the comparison happens on <i>every</i> request instead of
    /// twice an hour — without that line this is still "the next half hour", which is not what an
    /// operator revoking access believes they have done.</para>
    /// </summary>
    public async Task<MemberCommandResult> DisableMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await FindMemberAsync(tenantId, userId, cancellationToken);
        if (user is null) return MemberCommandResult.Refused(MemberRefusal.MemberNotFound);

        var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        try
        {
            // Identity refuses to set an end date on a user whose lockout is disabled, so the two
            // are one operation and are committed together — a row left enabled-without-an-end reads
            // as active on the screen and would sign in perfectly well.
            var enabled = await users.SetLockoutEnabledAsync(user, true);
            if (!enabled.Succeeded) return await FailAsync(transaction, enabled, cancellationToken);

            var locked = await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            if (!locked.Succeeded) return await FailAsync(transaction, locked, cancellationToken);

            var stamped = await users.UpdateSecurityStampAsync(user);
            if (!stamped.Succeeded) return await FailAsync(transaction, stamped, cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Disabled {Email} in workspace {TenantId}.", user.Email, tenantId);

            return MemberCommandResult.Ok(user.Email);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<MemberCommandResult> EnableMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await FindMemberAsync(tenantId, userId, cancellationToken);
        if (user is null) return MemberCommandResult.Refused(MemberRefusal.MemberNotFound);

        var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        try
        {
            var unlocked = await users.SetLockoutEndDateAsync(user, null);
            if (!unlocked.Succeeded) return await FailAsync(transaction, unlocked, cancellationToken);

            // The failed-attempt counter is what would lock them out again after a handful of
            // mistyped passwords, and somebody coming back from a suspension has no idea it is
            // there. Clearing it makes re-enabling mean what the button says.
            var counted = await users.ResetAccessFailedCountAsync(user);
            if (!counted.Succeeded) return await FailAsync(transaction, counted, cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Re-enabled {Email} in workspace {TenantId}.", user.Email, tenantId);

            return MemberCommandResult.Ok(user.Email);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<MemberCommandResult> ResetMemberPasswordAsync(
        Guid tenantId,
        Guid userId,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await FindMemberAsync(tenantId, userId, cancellationToken);
        if (user is null) return MemberCommandResult.Refused(MemberRefusal.MemberNotFound);

        // Generated and spent inside this call. The alternative — remove the password, then add the
        // new one — leaves an account with no password at all for as long as it takes the second
        // call to run, and permanently if the new password fails the policy. This path validates
        // first and replaces the hash only if it passed.
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password ?? string.Empty);

        if (!result.Succeeded)
        {
            return new MemberCommandResult(
                MemberRefusal.IdentityRefused, user.Email, [.. result.Errors.Select(e => e.Description)]);
        }

        // Identity bumps the security stamp when the hash changes, so this also ends every session
        // the old password had open — which is the point when the reason for the reset is that
        // somebody else knows the old one.
        logger.LogInformation("Reset the password for {Email} in workspace {TenantId}.", user.Email, tenantId);

        return MemberCommandResult.Ok(user.Email);
    }

    /// <summary>
    /// A member of <paramref name="tenantId"/> and nobody else. There is no overload that takes a
    /// user id alone, so an operator's own account — <c>TenantId</c> null — is not reachable from
    /// any of these commands, and neither is another customer's.
    /// </summary>
    private async Task<AppUser?> FindMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.TenantId == tenantId, cancellationToken);

    private static async Task<MemberCommandResult> FailAsync(
        IDbContextTransaction? transaction,
        IdentityResult result,
        CancellationToken cancellationToken)
    {
        if (transaction is not null) await transaction.RollbackAsync(cancellationToken);

        return new MemberCommandResult(
            MemberRefusal.IdentityRefused, null, [.. result.Errors.Select(e => e.Description)]);
    }
}
