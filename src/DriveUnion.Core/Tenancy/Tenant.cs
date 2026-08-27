using DriveUnion.Core.Plans;

namespace DriveUnion.Core.Tenancy;

/// <summary>
/// A customer workspace. Files, share links and upload sessions all belong to one.
///
/// There is no <c>Tenant</c> on <see cref="Storage.GoogleAccount"/> and that asymmetry is the whole
/// product: the Drive accounts belong to the operator, the files inside them belong to tenants.
///
/// <para><b>The tenant carries its own effective limits.</b> <see cref="PlanId"/> is a record of
/// which template was last applied and is read by no enforcement path; the four numbers below are
/// what every check actually compares against. See <see cref="Plan"/> for the argument, and
/// <c>ITenantPlanService</c> for the only thing allowed to write them.</para>
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

    /// <summary>
    /// When the operator stopped this workspace's public links from resolving, or null.
    ///
    /// <para><b>What it is for.</b> One customer publishing something that gets reported to Google
    /// costs every other customer on the same pool account their files, because Google suspends the
    /// account and not the file. So the operator needs one control that stops a whole workspace
    /// handing anything out, faster than they can revoke links one at a time.</para>
    ///
    /// <para><b>What it deliberately does not do.</b> It does not delete anything, does not lock the
    /// owner out of their own panel, and does not touch a single byte. The customer can still sign
    /// in, see their files, and take them away. Suspension is about what the <i>public</i> can
    /// reach — an accusation is not a finding, and a control that destroyed data on one would be a
    /// control nobody could afford to use quickly.</para>
    /// </summary>
    public DateTimeOffset? PublicSuspendedAt { get; set; }

    /// <summary>Why, in the operator's words. Never shown to a visitor — see the public card's rule.</summary>
    public string? PublicSuspendedReason { get; set; }

    /// <summary>Whether the public half of this workspace is switched off.</summary>
    public bool IsPubliclySuspended => PublicSuspendedAt is not null;

    public const int MaxSuspensionReasonLength = 512;

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // The effective limits.
    //
    // All four are non-nullable, and none of them means "unlimited". A nullable cap meaning
    // unlimited is one migration default away from every tenant being uncapped, and nothing looks
    // wrong until the pool is full. A "no practical limit" tier is a large explicit number.
    //
    // Each is initialised to the smallest seeded tier rather than to zero. A zero cap would refuse
    // every upload in the product the moment a row were created by anything that had not been
    // taught about plans — the sign-up path, a test fixture, a support insert — and a default has to
    // fail in the safe direction. The smallest tier is that direction; a generous one has exactly
    // the shape the paragraph above refuses.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which template was last applied, for the operator's screen. Null when a tenant has only ever
    /// had the numbers it was created with. <b>Nothing on any enforcement path reads it.</b>
    /// </summary>
    public Guid? PlanId { get; set; }

    /// <summary>When the copy happened. Null for the same reason as <see cref="PlanId"/>.</summary>
    public DateTimeOffset? PlanAppliedAt { get; set; }

    /// <summary>The ceiling on stored bytes. M5 §7's, with a plan now behind the number.</summary>
    public long StorageQuotaBytes { get; set; } = PlanCatalogue.Default.StorageBytes;

    /// <summary>
    /// Live files plus the declared size of every in-flight upload session.
    ///
    /// <para>In-flight sessions are counted, or ten parallel 60 GB uploads into a 500 GB cap each
    /// pass the check and land at 600 GB — a bug that only appears under a real user with a real
    /// connection, which is to say in production.</para>
    ///
    /// <para>Denormalised for the same reason <c>ShareLink.DownloadCount</c> is: it is read on every
    /// page render. The authoritative figure is the sum over the rows, and a reconciliation logs a
    /// discrepancy rather than silently correcting it, because a discrepancy means a transition was
    /// missed and that is worth seeing.</para>
    /// </summary>
    public long StorageUsedBytes { get; set; }

    /// <summary>The ceiling on one file. Enforced in <c>IUploadCoordinator.BeginAsync</c>.</summary>
    public long MaxFileBytes { get; set; } = PlanCatalogue.Default.MaxFileBytes;

    /// <summary>
    /// The monthly egress allowance. P1 carries and sells it; P2 meters it, because the counter, its
    /// window and the counting-stream wrap are a slice of their own and a column with no writer is a
    /// number on a screen that nobody honours.
    /// </summary>
    public long MonthlyEgressBytes { get; set; } = PlanCatalogue.Default.MonthlyEgressBytes;

    /// <summary>
    /// Seats. P1 carries and sells it; P3 enforces it at invitation creation, where the count and
    /// the insert become one conditional statement.
    /// </summary>
    public int MaxMembers { get; set; } = PlanCatalogue.Default.MaxMembers;
}
