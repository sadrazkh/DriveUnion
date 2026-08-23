namespace DriveUnion.Core.Tenancy;

/// <summary>
/// A customer workspace. Files, share links and upload sessions all belong to one.
///
/// There is no <c>Tenant</c> on <see cref="Storage.GoogleAccount"/> and that asymmetry is the whole
/// product: the Drive accounts belong to the operator, the files inside them belong to tenants.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// URL- and filename-safe. Used for the per-tenant folder inside each Drive account
    /// (<c>DriveUnion/{Slug}/</c>), so it must stay stable once files exist under it.
    /// </summary>
    public required string Slug { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
