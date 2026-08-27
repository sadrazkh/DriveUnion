using DriveUnion.Core.Sharing;

namespace DriveUnion.Core.Application;

/// <summary>Why a report was not accepted. Both are answered to the reporter identically — see below.</summary>
public enum AbuseReportRefusal
{
    None = 0,

    /// <summary>No such link, or one that is already gone.</summary>
    UnknownLink = 1,

    /// <summary>This link already has as many open reports as the queue will hold.</summary>
    AlreadyReported = 2,
}

public sealed record AbuseReportResult(Guid? ReportId, AbuseReportRefusal Refusal);

/// <summary>One report as the operator's queue shows it.</summary>
/// <param name="Slug">
/// The public address, so the operator can open the link and look — which is how they judge a
/// report, and deliberately the only way.
///
/// <para>There is no privileged viewer in this product and this feature does not add one. The
/// reporter saw the file through a public link; the operator can see it through the same link. A
/// screen that let an operator open any customer's file on the strength of an accusation would be a
/// far larger thing than the problem it solves.</para>
/// </param>
public sealed record AbuseReportView(
    Guid Id,
    string Slug,
    string FileName,
    Guid TenantId,
    string TenantName,
    bool TenantSuspended,
    bool LinkRevoked,
    int TenantOpenReports,
    AbuseKind Kind,
    string? Note,
    string? ReporterEmail,
    AbuseReportStatus Status,
    string? Resolution,
    DateTimeOffset CreatedAt);

/// <summary>
/// The anonymous half: a visitor telling the operator something is wrong.
///
/// <para>No tenant argument anywhere, because the reporter has no workspace and usually no account.
/// What they have is a slug, which is all they ever saw.</para>
/// </summary>
public interface IAbuseReports
{
    /// <summary>
    /// Files a report against a public link.
    ///
    /// <para><b>Every outcome looks the same to the reporter</b>, including the refusals. A form that
    /// answered «no such link» would confirm which slugs exist to anybody willing to type — the same
    /// enumeration the public card's single refusal exists to prevent, and it would be a strange
    /// thing to reopen through the abuse form of all places.</para>
    /// </summary>
    Task<AbuseReportResult> FileAsync(
        string slug,
        AbuseKind kind,
        string? note,
        string? reporterEmail,
        string? reporterIpHash,
        CancellationToken cancellationToken);
}

/// <summary>
/// The operator's half: the queue, and the two things that can be done about a report.
///
/// <para>Reachable only from the operator panel. Nothing here is scoped to a tenant, because the
/// whole point is looking across all of them.</para>
/// </summary>
public interface IAbuseQueue
{
    // Every «operatorUserId» below is nullable, and that is not laziness about an audit field. It is
    // read off the signed-in principal and nowhere else, and null is what a principal with no user id
    // claim produces. The alternatives were both worse: Guid.Empty writes down a person who does not
    // exist and looks exactly like one who does, and throwing turns a missing claim into a 500 on the
    // one screen an operator reaches for when a customer's file is about to cost them the pool.
    // A row that says «resolved, by we-do-not-know-who» is honest and still resolved.

    /// <param name="openOnly">
    /// The default view. A queue that showed everything ever resolved would be a queue nobody could
    /// see the top of.
    /// </param>
    Task<IReadOnlyList<AbuseReportView>> ListAsync(bool openOnly, CancellationToken cancellationToken);

    /// <summary>How many reports are waiting, for the badge on the operator's nav.</summary>
    Task<int> OpenCountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes the link down and closes the report.
    ///
    /// <para>Revoking rather than deleting the file: the accusation may be wrong, the customer may
    /// appeal, and a revoked link stops the public reaching it just as completely. Deleting is
    /// available to the operator through the workspace, and is a separate decision made with more
    /// than a stranger's say-so.</para>
    /// </summary>
    Task<bool> UpholdAsync(
        Guid reportId,
        Guid? operatorUserId,
        string? resolution,
        CancellationToken cancellationToken);

    /// <summary>Closes the report and touches nothing.</summary>
    Task<bool> RejectAsync(
        Guid reportId,
        Guid? operatorUserId,
        string? resolution,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops every public link this workspace has, at once.
    ///
    /// <para>The blunt instrument, for when one file is not the problem. It hides nothing from the
    /// owner and deletes nothing — see <c>Tenant.PublicSuspendedAt</c>.</para>
    /// </summary>
    Task<bool> SuspendTenantAsync(
        Guid tenantId,
        Guid? operatorUserId,
        string? reason,
        CancellationToken cancellationToken);

    Task<bool> RestoreTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
