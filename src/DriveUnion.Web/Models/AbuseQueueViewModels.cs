using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// The operator's queue of complaints, already turned into sentences.
///
/// <para>Every field here is a string the view prints, and none of it is an enum the view switches
/// on. That is the same rule the rest of the panel keeps: <c>UiText</c> is read in C# where
/// <c>LocalizationCatalogueTests</c> can render it in both languages, and a <c>switch</c> in a
/// <c>.cshtml</c> is a translation nothing checks.</para>
/// </summary>
/// <param name="ShowingAll">
/// Which of the two lists is on screen, so the toggle can say which one it is offering rather than
/// which one you are looking at.
/// </param>
public sealed record AbuseQueuePageViewModel(
    IReadOnlyList<AbuseRowViewModel> Reports,
    bool ShowingAll,
    int OpenCount,
    string? Message);

/// <param name="TenantId">
/// Carried because the suspend button acts on the workspace and not on the report. It is a route
/// value on a POST behind the operator policy, never anything a reader supplies.
/// </param>
/// <param name="IsOpen">
/// Whether there is still a decision to make. A resolved row keeps its place in the «all» list — the
/// history is the point of that list — but shows what was decided instead of the buttons.
/// </param>
public sealed record AbuseRowViewModel(
    Guid Id,
    Guid TenantId,
    string Slug,
    string FileName,
    string TenantName,
    bool TenantSuspended,
    bool LinkRevoked,
    string? OtherReportsText,
    string KindText,
    string? Note,
    string? ReporterEmail,
    bool IsOpen,
    string StatusText,
    string? Resolution,
    string WhenText)
{
    public static AbuseRowViewModel From(AbuseReportView report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new AbuseRowViewModel(
            report.Id,
            report.TenantId,
            report.Slug,
            report.FileName,
            report.TenantName,
            report.TenantSuspended,
            report.LinkRevoked,

            // Only when there is more than this one. «1 waiting from this workspace» beside the
            // single report it is counting is a number that reads as news and is not.
            report.TenantOpenReports > 1
                ? $"{Numerals.Count(report.TenantOpenReports)} {UiText.Abuse.ReportsFromThisWorkspace}"
                : null,
            Describe(report.Kind),
            report.Note,
            report.ReporterEmail,
            report.Status == AbuseReportStatus.Open,
            report.Status switch
            {
                AbuseReportStatus.Upheld => UiText.Abuse.Upheld,
                AbuseReportStatus.Rejected => UiText.Abuse.Rejected,
                _ => UiText.Abuse.Waiting,
            },
            report.Resolution,
            DisplayFormats.PanelDateTime(report.CreatedAt));
    }

    /// <summary>
    /// The five kinds, in the operator's words rather than the reporter's.
    ///
    /// <para>The form's own labels are first person — «It is my work, published without permission» —
    /// which is right where somebody is choosing one and wrong in a column, where it reads as the
    /// panel asserting the claim is true.</para>
    /// </summary>
    private static string Describe(AbuseKind kind) => kind switch
    {
        AbuseKind.Copyright => UiText.Abuse.ShortCopyright,
        AbuseKind.Malware => UiText.Abuse.ShortMalware,
        AbuseKind.Illegal => UiText.Abuse.ShortIllegal,
        AbuseKind.Privacy => UiText.Abuse.ShortPrivacy,
        _ => UiText.Abuse.ShortOther,
    };
}
