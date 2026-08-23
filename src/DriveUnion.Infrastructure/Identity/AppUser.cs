using Microsoft.AspNetCore.Identity;

namespace DriveUnion.Infrastructure.Identity;

/// <summary>
/// A person who signs into the panel.
///
/// <see cref="TenantId"/> is null for operator staff and set for everybody else. Two audiences,
/// one panel: the operator sees the pool, its quotas and every tenant; a customer sees their own
/// files and never learns that Google is involved at all.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public Guid? TenantId { get; set; }

    public bool IsOperator { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
