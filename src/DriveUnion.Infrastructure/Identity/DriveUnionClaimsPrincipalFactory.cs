using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Identity;

/// <summary>
/// Puts <see cref="AppUser.TenantId"/> and <see cref="AppUser.IsOperator"/> onto the signed-in
/// principal. Without it the panel's two policies have nothing to read and every route refuses —
/// which is the right failure direction, but it means the panel is unreachable until this runs.
///
/// The claim names are duplicated from <c>DriveUnion.Web.Security.DriveUnionClaimTypes</c> rather
/// than shared, because Web references Infrastructure and not the other way round. That duplication
/// is the one thing here that can rot silently, so a test pins the two spellings to each other and
/// then evaluates the real policies against what this factory produces.
/// </summary>
public sealed class DriveUnionClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    public const string TenantIdClaimType = "drive_union:tenant_id";

    public const string OperatorClaimType = "drive_union:operator";

    public const string OperatorClaimValue = "true";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        // Operator staff have no tenant, and the two claims are mutually exclusive here even though
        // a well-formed row could never carry both. A staff account that somehow acquired a TenantId
        // would otherwise be handed that tenant's whole file catalogue; refusing the tenant claim
        // costs the operator nothing, because the pool screens are what they came for.
        if (user.IsOperator)
        {
            identity.AddClaim(new Claim(OperatorClaimType, OperatorClaimValue));
            return identity;
        }

        // Guid.Empty is not a tenant. It is what a missing value parses to, and a request scoped to
        // it reads an empty database while looking perfectly healthy — the failure §8 of the design
        // is written to prevent. The policy rejects it too; it is refused at the source as well so
        // the string never reaches anything that reads the raw claim.
        if (user.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            identity.AddClaim(new Claim(TenantIdClaimType, tenantId.ToString()));
        }

        return identity;
    }
}
