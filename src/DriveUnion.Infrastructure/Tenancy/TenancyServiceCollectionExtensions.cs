using DriveUnion.Core.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// The operator's workspace and account management: the cross-tenant read model and the one
    /// writer that creates a workspace, creates the people in it, and takes their access away.
    ///
    /// <para>Scoped, because both share the request's <c>DriveUnionDbContext</c> — and because
    /// <c>ITenantPlanService</c>, which the provisioner calls to give a new workspace its plan, is
    /// scoped for the same reason and the two have to be looking at one change tracker.</para>
    ///
    /// <para>Must be registered <b>after</b> <c>AddIdentity</c> and after <c>AddDriveUnionPlans</c>:
    /// it resolves <c>UserManager&lt;AppUser&gt;</c> and <c>ITenantPlanService</c> and registers
    /// neither.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Also registered by AddGoogleDrive and AddDriveUnionPlans; TryAdd so the order of the calls
        // does not decide which clock the panel keeps.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<IOperatorTenantDirectory, OperatorTenantDirectory>();
        services.TryAddScoped<ITenantProvisioning, TenantProvisioning>();

        // ── Why disabling somebody takes effect on their next request ────────────────────────────
        //
        // A disabled account is locked out, so nobody signs in again. But the person being disabled
        // is usually signed in at that moment, and an authentication cookie is a self-contained
        // credential: the server does not go back to the database to ask whether it is still true.
        // Left alone, the session an operator just revoked keeps working until the cookie expires.
        //
        // Identity's answer is the security stamp, which TenantProvisioning bumps on every disable
        // and every password reset. SecurityStampValidator compares the stamp in the cookie against
        // the row and rejects the principal when they differ — but by default it only does that
        // every thirty minutes, so "revoked" would mean "revoked within half an hour", which is not
        // what an operator pressing the button believes.
        //
        // Zero makes the comparison happen on every request. The cost is one indexed lookup by user
        // id per authenticated request, which is the same cost M5 §4 already accepted when it ruled
        // that role and tenant are read from the database rather than trusted from cookie claims.
        // Anonymous requests — /d/{slug}, the public download, the sign-in page — carry no cookie
        // and are untouched.
        services.Configure<SecurityStampValidatorOptions>(
            options => options.ValidationInterval = TimeSpan.Zero);

        return services;
    }
}
