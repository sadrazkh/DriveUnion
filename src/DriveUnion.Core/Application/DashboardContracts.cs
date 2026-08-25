using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>
/// One file a workspace uploaded recently, for the dashboard's «آخرین آپلودها» list.
///
/// <para>It carries no <c>GoogleAccountId</c>, no <c>DriveFileId</c> and no folder. M1 §1.4: the
/// Google accounts are the operator's and a customer must never learn which one holds their file —
/// so the record a customer's screen is built from cannot name one, which is a stronger guarantee
/// than a view that remembers not to print it.</para>
/// </summary>
public sealed record RecentUpload(Guid FileId, string Name, long SizeBytes, DateTimeOffset UploadedAt);

/// <summary>
/// One of the workspace's own links, with what it has served.
/// </summary>
/// <param name="MaxDownloads">
/// Null where the link has no cap. The screen draws a count on its own rather than inventing an
/// infinity the customer never chose.
/// </param>
public sealed record BusyLink(
    Guid FileId,
    string FileName,
    string Slug,
    int DownloadCount,
    int? MaxDownloads);

/// <summary>
/// «داشبورد» for the workspace whose figures they are: what is stored, what was uploaded, which
/// links are live, what has been downloaded, and what the trash is still holding.
///
/// <para>Every figure here is the customer's own and every one of them is counted from rows that
/// exist. There is deliberately no traffic-spent number: the meter, its window and its counting
/// stream are P2's, and a zero would read as «you have used none» to a customer who has been serving
/// downloads all month. The screen draws a dash there, which is the same answer the sidebar's
/// capacity card already gives for the same reason.</para>
/// </summary>
/// <param name="Plan">
/// The workspace's effective limits and what it has spent, from the same service the plan screen and
/// the sidebar's capacity card read. Reused rather than recomputed so the three cannot come to
/// disagree about how full a workspace is.
/// </param>
/// <param name="TrashBytes">
/// What the trash is holding, from <see cref="ITrash.SizeAsync"/>. It is on a dashboard for the
/// reason it is on the capacity card: it is exactly the difference between what a customer believes
/// they freed and what they actually did.
/// </param>
/// <param name="DownloadsAllTime">
/// The sum of <c>ShareLink.DownloadCount</c>, which is the same counter the public page increments.
///
/// <para><b>There is deliberately no «this month» beside it.</b> A window figure has to be counted
/// from <c>DownloadEvent</c> rows, that table is indexed on <c>(ShareLinkId, OccurredAt)</c>, and the
/// date can only be judged in memory — SQLite refuses to compare a <c>DateTimeOffset</c> in SQL and
/// this layer runs on SQLite in the tests. So the window would mean reading every download a
/// workspace has ever served, on the panel's most-visited page. The per-link counts in
/// <paramref name="BusiestLinks"/> are what answers «what is being downloaded» in the meantime; the
/// window arrives with the index or the rolled-up counter that makes it affordable, and both are
/// migrations.</para>
/// </param>
public sealed record CustomerDashboard(
    TenantPlanView Plan,
    long TrashBytes,
    int TrashFileCount,
    int LiveLinkCount,
    int LinkCount,
    long DownloadsAllTime,
    IReadOnlyList<RecentUpload> RecentUploads,
    IReadOnlyList<BusyLink> BusiestLinks);

/// <summary>
/// The customer's dashboard, scoped by an explicit argument like everything else in the panel.
///
/// <para>There is no unscoped overload and no nullable tenantId meaning «every workspace». This
/// product has no global query filter — <c>/d/{slug}</c> is anonymous and a filter would hand it
/// <c>Guid.Empty</c> — so a forgotten scope has to be a compile error rather than an empty result
/// set, or worse, somebody else's figures.</para>
/// </summary>
public interface ICustomerDashboard
{
    /// <summary>
    /// Null when the claim names a workspace the database does not have. That is a fault rather than
    /// a screen of zeroes: a dashboard that renders zeroes for a workspace that does not exist is how
    /// a broken session comes to read as a customer with an empty account.
    /// </summary>
    Task<CustomerDashboard?> ReadAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>One Google account in the operator's pool, as the dashboard's card reads it.</summary>
/// <param name="QuotaTotalBytes">
/// What Google says the account holds. Zero for an account whose quota has never been read, which
/// the card draws as an absent bar rather than as a full one.
/// </param>
public sealed record PoolAccount(
    Guid Id,
    string Email,
    string Label,
    GoogleAccountStatus Status,
    long QuotaUsedBytes,
    long QuotaTotalBytes);

/// <summary>
/// A workspace close enough to its storage cap that the operator should know before the customer
/// does.
/// </summary>
public sealed record WorkspacePressure(
    Guid TenantId,
    string Name,
    long StorageUsedBytes,
    long StorageQuotaBytes);

/// <summary>
/// An upload that failed, for the «کارهای ناموفق» card.
/// </summary>
/// <param name="Reason">
/// Whatever failed the session, in the words it arrived in — usually Google's. Not translated and
/// not paraphrased: it is a diagnostic the operator will search for, and a paraphrase of an API
/// error is worth less than the string that can be pasted into a search box. It is rendered on this
/// screen and no other, for the same reason <c>GoogleAccount.LastFailureReason</c> is: it can carry
/// a session URI or the address of an account a customer must never learn about.
/// </param>
public sealed record FailedTransfer(
    Guid SessionId,
    string FileName,
    long SizeBytes,
    DateTimeOffset FailedAt,
    string? Reason);

/// <summary>
/// «داشبورد» for the operator: the pool, who is running out, and what is broken.
///
/// <para>It is built to answer two questions at a glance, because those are the two an operator
/// actually has — <b>is storage running out</b>, and <b>is anything broken</b>. Everything on it
/// serves one of the two, and nothing on it is a figure nobody meters.</para>
///
/// <para><b>There is no daily-upload figure and no egress chart</b>, and both absences are
/// deliberate. Google allows each account 750 GB of upload a day and this product counts none of it;
/// the monthly traffic a plan sells is carried on the tenant row and metered by nobody. Drawing
/// either as a zero, or as a bar that is always empty, would be the dashboard telling an operator
/// their pool is idle on the day it is not. The screen says the words instead.</para>
/// </summary>
/// <param name="PoolTotalBytes">
/// What the connected accounts hold between them. A disconnected account is not capacity — nothing
/// can be written to it — so it is left out, which is the same rule <see cref="IOperatorPlanReader"/>
/// already applies to the commitment figure.
/// </param>
/// <param name="CommittedStorageBytes">
/// <c>sum(Tenant.StorageQuotaBytes)</c>. Over-commitment is allowed and displayed rather than
/// prevented: caps are per-customer ceilings, not reservations.
/// </param>
/// <param name="TransfersInFlight">
/// Sessions Google would still accept a chunk for. An expired session is not in flight — its
/// resumable URI is dead — so counting it would put a permanent number on a card whose whole job is
/// to be zero when nothing is happening.
/// </param>
/// <param name="NearCeilingPercent">
/// The threshold <see cref="WorkspacesNearTheirCeiling"/> was selected with, carried so the screen
/// prints the number that actually chose the list rather than a literal of its own that can drift
/// away from it.
/// </param>
public sealed record OperatorDashboard(
    IReadOnlyList<PoolAccount> Accounts,
    long PoolUsedBytes,
    long PoolTotalBytes,
    int DisconnectedAccountCount,
    int WorkspaceCount,
    long CommittedStorageBytes,
    IReadOnlyList<WorkspacePressure> WorkspacesNearTheirCeiling,
    int NearCeilingPercent,
    int TransfersInFlight,
    int TransfersFailedInWindow,
    int FailureWindowHours,
    IReadOnlyList<FailedTransfer> RecentFailures)
{
    public bool IsOverCommitted => CommittedStorageBytes > PoolTotalBytes;
}

/// <summary>
/// The operator's dashboard. Aggregates and pool rows, and never a customer's file.
///
/// <para>It takes no tenant argument at all — not even a nullable one meaning «all of them» — for
/// the reason <see cref="IOperatorPlanReader"/> gives: a nullable tenantId on a scoped method is one
/// null reference away from being every customer's default.</para>
/// </summary>
public interface IOperatorDashboard
{
    Task<OperatorDashboard> ReadAsync(CancellationToken cancellationToken);
}
