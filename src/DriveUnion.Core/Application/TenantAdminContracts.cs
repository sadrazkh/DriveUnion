using DriveUnion.Core.Plans;

namespace DriveUnion.Core.Application;

/// <summary>
/// One workspace on the operator's list: what it is called, where its files live inside every Drive
/// account, what it is allowed to spend and what it has spent.
///
/// <para><see cref="Slug"/> is here and is on no customer-facing shape anywhere. It is the folder
/// name inside the operator's Google accounts (<c>DriveUnion/{slug}/</c>), so it is operator
/// vocabulary — and M1 §1.4 makes it a hard rule that a customer must never learn which Google
/// account holds their file, of which the folder layout is half the answer.</para>
/// </summary>
public sealed record TenantListing(
    Guid TenantId,
    string Name,
    string Slug,
    string? PlanCode,
    int MemberCount,
    int MaxMembers,
    long StorageUsedBytes,
    long StorageQuotaBytes,
    int FileCount,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Storage spent as a percentage of the cap, clamped so an over-cap workspace cannot overflow a
    /// progress track. A cap of zero reads as full rather than as a division by zero — the same rule
    /// <see cref="TenantPlanView.StoragePercent"/> states, because the two bars are the same bar.
    /// </summary>
    public double StoragePercent => StorageQuotaBytes <= 0
        ? 100d
        : Math.Clamp(StorageUsedBytes * 100d / StorageQuotaBytes, 0d, 100d);

    /// <summary>True when no further member may be created. The reason <c>MaxMembers</c> exists.</summary>
    public bool SeatsAreFull => MemberCount >= MaxMembers;
}

/// <summary>
/// One person inside a workspace, as the operator's screen reads them.
///
/// <para>There is no password, no hash and no stamp on this shape, and there is deliberately no
/// route that would put one here. What the operator may do to a credential is set a new one; what
/// they may read of it is nothing.</para>
/// </summary>
/// <param name="IsDisabled">
/// Computed from ASP.NET Identity's own lockout — <c>LockoutEnabled</c> and a <c>LockoutEnd</c> in
/// the future. There is no <c>IsDisabled</c> column, on purpose: a second flag beside the one the
/// framework already refuses sign-ins on is two answers to one question, and the day they disagree
/// the screen says «فعال» about somebody who cannot sign in.
/// </param>
public sealed record TenantMemberListing(
    Guid UserId,
    string Email,
    string? DisplayName,
    bool IsDisabled,
    DateTimeOffset? DisabledUntil,
    DateTimeOffset CreatedAt);

/// <summary>One workspace and everybody in it. The members are read <c>WHERE TenantId = @tenantId</c>.</summary>
public sealed record TenantWorkspaceView(
    Guid TenantId,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TenantMemberListing> Members);

/// <summary>
/// Why a workspace was not created. Each value is a sentence on the operator's screen, never a
/// database error: «this address is already a workspace» is actionable and
/// <c>23505: duplicate key value violates unique constraint "IX_Tenants_Slug"</c> is not.
/// </summary>
public enum TenantRefusal
{
    None,

    NameRequired,

    /// <summary>
    /// The slug is not one this product would mint. It is a folder name in somebody else's Drive
    /// and a URL segment, so the rule is strict and stated on the form before it is enforced here.
    /// </summary>
    SlugMalformed,

    /// <summary>
    /// Another workspace already owns that folder. Two tenants sharing one would put two customers'
    /// files in one directory inside the operator's Drive, which nothing downstream could untangle.
    /// </summary>
    SlugTaken,

    /// <summary>No such plan, or a retired one. Nothing was written.</summary>
    PlanNotFound,
}

/// <summary>Why a member command did nothing.</summary>
public enum MemberRefusal
{
    None,

    /// <summary>No such workspace. Nothing was written.</summary>
    TenantNotFound,

    EmailRequired,

    /// <summary>
    /// The workspace is at <c>Tenant.MaxMembers</c>. <b>Refused before the account is created</b>,
    /// which is the whole reason the column exists — an account made and then apologised for is an
    /// account that can sign in.
    /// </summary>
    SeatsFull,

    /// <summary>
    /// ASP.NET Identity would not have it: a taken address, or a password below the policy.
    /// <c>Errors</c> carries Identity's own sentences, already in the panel's language.
    /// </summary>
    IdentityRefused,

    /// <summary>
    /// No such member <i>in this workspace</i>. An id belonging to another tenant, and an operator's
    /// own account, both land here — the lookup is <c>WHERE Id = @userId AND TenantId = @tenantId</c>
    /// and there is no unscoped overload of it.
    /// </summary>
    MemberNotFound,
}

/// <summary>What a created workspace turned out to be, once the slug had been normalised.</summary>
public sealed record TenantProvisioned(Guid TenantId, string Name, string Slug, PlanNumbers Limits);

public sealed record TenantProvisioningResult(TenantRefusal Refusal, TenantProvisioned? Tenant)
{
    public static TenantProvisioningResult Refused(TenantRefusal refusal) => new(refusal, null);
}

/// <param name="SeatsUsed">Members at the moment of the attempt, for the sentence the cap refuses with.</param>
public sealed record MemberProvisioningResult(
    MemberRefusal Refusal,
    Guid? UserId,
    IReadOnlyList<string> Errors,
    int SeatsUsed,
    int MaxMembers)
{
    public static MemberProvisioningResult Refused(MemberRefusal refusal) =>
        new(refusal, null, [], 0, 0);
}

/// <summary>Disabling, re-enabling and setting a password all answer the same shape.</summary>
/// <param name="Email">
/// Who it happened to, so the screen can say a name rather than a id. Null on every refusal — the
/// address of somebody a command did not find is not a thing this returns.
/// </param>
public sealed record MemberCommandResult(
    MemberRefusal Refusal,
    string? Email,
    IReadOnlyList<string> Errors)
{
    public static MemberCommandResult Ok(string? email) => new(MemberRefusal.None, email, []);

    public static MemberCommandResult Refused(MemberRefusal refusal) => new(refusal, null, []);
}

/// <summary>
/// The operator's cross-tenant view of workspaces and the people in them. Aggregates and members,
/// and <b>never a file row across tenants</b> — an operator inspecting one customer's files does it
/// through the same tenant-scoped repository the customer's own request would call.
///
/// <para><see cref="GetAsync"/> takes the tenant from the route and hands it on explicitly. There is
/// deliberately no unscoped overload and no nullable tenantId meaning "every workspace": that
/// signature is one null reference away from being every customer's default, which is the failure
/// M1 §8 spent its whole argument avoiding. <see cref="ListAsync"/> takes no tenant argument at all
/// rather than a nullable one, for the same reason.</para>
/// </summary>
public interface IOperatorTenantDirectory
{
    Task<IReadOnlyList<TenantListing>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Null when there is no such workspace.</summary>
    Task<TenantWorkspaceView?> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// <b>The only thing outside the configuration seeder that creates a workspace or an account.</b>
///
/// <para>Before this existed the product could not onboard a customer at all: the sole route in was
/// four environment variables and a redeploy, and a grep for <c>new Tenant</c> and
/// <c>CreateAsync</c> across <c>src/</c> found hits under <c>Infrastructure/Seeding</c> and nowhere
/// else. The seeder is how an empty database gets its first operator; this is everything after
/// that.</para>
///
/// <para><b>Nothing here writes <c>AppUser.IsOperator</c> as anything but false.</b> M5
/// §8 leaves the config seeder as its one writer in the whole codebase, and every method below binds
/// an explicit request shape rather than an entity, so an over-posted <c>isOperator=true</c> has
/// nothing to land on.</para>
///
/// <para><b>There is no <c>DeleteTenantAsync</c>, and its absence is a decision.</b> Nothing in this
/// schema has a foreign key from a tenant's rows back to <c>Tenants</c> — scoping here is an
/// explicit argument, not a relationship — so removing the row would not fail loudly, it would
/// succeed and orphan every file, link, upload session and account that named it. The bytes are
/// real and sit inside the operator's Google accounts; the row is the only thing that names them.
/// Disabling every member is the reversible version of the same intent, and it is what the workspace
/// page offers instead.</para>
/// </summary>
public interface ITenantProvisioning
{
    /// <summary>
    /// A workspace and the plan it starts on, in one transaction. A workspace with no plan falls
    /// back to column defaults, which works and is nobody's decision — so a failure to apply the
    /// plan takes the workspace with it rather than leaving one behind.
    /// </summary>
    /// <param name="planCode">
    /// Empty applies <c>Plans:DefaultPlanCode</c>. Anything else must name a live tier.
    /// </param>
    Task<TenantProvisioningResult> CreateTenantAsync(
        string name,
        string slug,
        string? planCode,
        Guid? createdByUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// An account inside a workspace, with a password the operator sets. Refused at
    /// <c>Tenant.MaxMembers</c> before anything is created.
    /// </summary>
    Task<MemberProvisioningResult> CreateMemberAsync(
        Guid tenantId,
        string email,
        string? displayName,
        string password,
        CancellationToken cancellationToken);

    /// <summary>
    /// Locks the account out and rebuilds its principal, so a session already open stops working on
    /// its next request rather than at its next sign-in.
    /// </summary>
    Task<MemberCommandResult> DisableMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<MemberCommandResult> EnableMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets a new password. The old one is not read, not needed and not recoverable — there is no
    /// password reset in the panel, so this is the whole of "I have lost mine".
    /// </summary>
    Task<MemberCommandResult> ResetMemberPasswordAsync(
        Guid tenantId,
        Guid userId,
        string password,
        CancellationToken cancellationToken);
}
