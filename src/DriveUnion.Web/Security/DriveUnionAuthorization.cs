using System.Security.Claims;

namespace DriveUnion.Web.Security;

/// <summary>
/// What the panel authorises on.
///
/// Identity holds <c>TenantId</c> and <c>IsOperator</c> on the user row; something has to project
/// them onto the principal or every panel route refuses. That failure direction is the point: a
/// session with no tenant claim is locked out rather than quietly scoped to <c>Guid.Empty</c>, which
/// is the exact shape of the bug §8 of the design was written to avoid.
/// </summary>
public static class DriveUnionClaimTypes
{
    public const string TenantId = "drive_union:tenant_id";

    public const string Operator = "drive_union:operator";

    /// <summary>The only value of <see cref="Operator"/> that grants anything.</summary>
    public const string OperatorValue = "true";
}

public static class DriveUnionPolicies
{
    /// <summary>A signed-in customer whose tenant is resolvable. Every panel route requires it.</summary>
    public const string Tenant = "DriveUnion.Tenant";

    /// <summary>
    /// Operator staff. The Google pool is theirs, and a customer must get a 403 rather than a
    /// missing link — the whole product model rests on customers never learning that Google is
    /// involved, and a hidden button is not an access control.
    /// </summary>
    public const string Operator = "DriveUnion.Operator";
}

public static class DriveUnionPrincipalExtensions
{
    /// <summary>
    /// Null when the caller has no usable tenant. <c>Guid.Empty</c> is treated as no tenant on
    /// purpose: it is what a missing claim parses to if anyone ever reaches for
    /// <c>Guid.Parse</c> defaults, and a request scoped to it reads an empty database while looking
    /// perfectly healthy.
    /// </summary>
    public static Guid? GetTenantId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(DriveUnionClaimTypes.TenantId);
        return Guid.TryParse(raw, out var tenantId) && tenantId != Guid.Empty ? tenantId : null;
    }

    public static bool IsOperator(this ClaimsPrincipal principal) =>
        principal.HasClaim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue);
}
