namespace DriveUnion.Infrastructure.Seeding;

/// <summary>
/// How the first accounts get into an empty database.
///
/// Every property is optional and every one of them is inert on its own: nothing is created unless
/// an email <em>and</em> its password are both present. The passwords are never read from a file in
/// this repository — they come from <c>dotnet user-secrets</c> in development and from the
/// environment in production, which is why they are not in appsettings.json and must not be put
/// there.
/// </summary>
public sealed class DriveUnionSeedOptions
{
    public const string SectionName = "DriveUnion:Seed";

    /// <summary>The first operator. Nothing is seeded at all when this is empty.</summary>
    public string? OperatorEmail { get; set; }

    /// <summary>
    /// Supplied out of band. There is no default and none is generated: an invented password is
    /// either printed somewhere it can be read later or lost the moment the process exits.
    /// </summary>
    public string? OperatorPassword { get; set; }

    public string? OperatorDisplayName { get; set; }

    /// <summary>
    /// A development workspace, so the tenant half of the panel can be exercised without waiting
    /// for M5's tenant creation. The slug names the per-tenant folder inside every Drive account,
    /// so an existing tenant with this slug is left exactly as it is — renaming it would orphan
    /// every file already stored under the old folder.
    /// </summary>
    public string? TenantSlug { get; set; }

    public string? TenantName { get; set; }

    /// <summary>A member of the seeded tenant. Needs <see cref="TenantSlug"/> to have anywhere to go.</summary>
    public string? TenantUserEmail { get; set; }

    public string? TenantUserPassword { get; set; }

    public string? TenantUserDisplayName { get; set; }
}
