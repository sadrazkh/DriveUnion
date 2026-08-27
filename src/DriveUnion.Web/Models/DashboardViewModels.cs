using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One row of «آخرین آپلودها». <see cref="FileId"/> is not drawn — it is where the row goes.
/// </summary>
public sealed record RecentUploadRowViewModel(
    Guid FileId,
    string Name,
    string SizeText,
    string WhenText,
    string OpenLabel);

/// <summary>
/// One row of «پربازدیدترین لینک‌ها»: the file, its address, and what it has served.
/// </summary>
/// <param name="DownloadsText">
/// «۲۴۱ / ۵۰۰» where the customer set a cap, and a bare count where they did not. There is no ∞ in
/// the second case: the customer never chose one, and a symbol they did not set reads as a promise
/// the product is making.
/// </param>
public sealed record BusyLinkRowViewModel(
    Guid FileId,
    string FileName,
    string SlugPath,
    string DownloadsText);

/// <summary>
/// «داشبورد» as the customer's own screen renders it.
///
/// <para>Nothing here names a Google account, the pool, its daily allowance or another workspace,
/// and nothing here can: every field is built from <see cref="CustomerDashboard"/>, which carries
/// none of them. M1 §1.4 — a customer must never learn which account holds their file or that a
/// pool exists — is enforced by the shape of the record rather than by a view remembering.</para>
///
/// <para>Every member is a string that has already been formatted, for the reason
/// <c>ShellCapacity</c> gives: arithmetic in a view is arithmetic no test can reach.</para>
/// </summary>
public sealed class CustomerDashboardPageViewModel
{
    public CustomerDashboardPageViewModel(CustomerDashboard dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        var plan = dashboard.Plan;

        PlanName = plan.PlanName ?? UiText.Plans.NoPlan;

        StorageText = UiText.Plans.OfCap(
            DisplayFormats.Bytes(plan.StorageUsedBytes),
            DisplayFormats.Bytes(plan.Limits.StorageBytes));

        StoragePercent = PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes);
        StorageFillClass = PlanMeter.FillClass(StoragePercent);
        IsOverStorage = plan.IsOverStorage;

        FilesText = UiText.Dashboard.FilesStored(plan.FileCount);

        // Spent against the allowance, through the same entry the sidebar's capacity card uses —
        // which is what keeps the two from drifting into two answers about one month. It was a dash
        // on both until ITrafficMeter existed to fill it.
        TrafficText = UiText.Capacity.TrafficOfCap(
            DisplayFormats.Bytes(dashboard.TrafficThisMonth.EgressBytes),
            DisplayFormats.Bytes(plan.Limits.MonthlyEgressBytes));

        TrafficPercent = PlanMeter.Percent(dashboard.TrafficThisMonth.EgressBytes, plan.Limits.MonthlyEgressBytes);
        TrafficFillClass = PlanMeter.FillClass(TrafficPercent);

        HasLinks = dashboard.LinkCount > 0;
        LiveLinksText = UiText.Dashboard.LiveOfTotal(dashboard.LiveLinkCount, dashboard.LinkCount);

        DownloadsAllTimeText = Numerals.Count(dashboard.DownloadsAllTime);

        TrashText = DisplayFormats.Bytes(dashboard.TrashBytes);
        HasTrash = dashboard.TrashFileCount > 0;
        TrashSummary = HasTrash
            ? UiText.Dashboard.TrashHolds(dashboard.TrashFileCount)
            : UiText.Dashboard.TrashIsEmpty;

        var now = DateTimeOffset.UtcNow;

        RecentUploads =
        [
            .. dashboard.RecentUploads.Select(upload => new RecentUploadRowViewModel(
                upload.FileId,
                upload.Name,
                DisplayFormats.Bytes(upload.SizeBytes),
                DisplayFormats.Relative(upload.UploadedAt, now),
                UiText.Dashboard.OpenFile(upload.Name))),
        ];

        BusiestLinks =
        [
            .. dashboard.BusiestLinks.Select(link => new BusyLinkRowViewModel(
                link.FileId,
                link.FileName,
                PublicLinkFormatter.Path(link.Slug),
                link.MaxDownloads is { } cap
                    ? UiText.Dashboard.DownloadsOfCap(link.DownloadCount, cap)
                    : UiText.Dashboard.DownloadsUncapped(link.DownloadCount))),
        ];
    }

    public string PlanName { get; }

    public string StorageText { get; }

    public double StoragePercent { get; }

    public string StorageFillClass { get; }

    public bool IsOverStorage { get; }

    public string FilesText { get; }

    public string TrafficText { get; }

    /// <summary>
    /// The traffic bar, which the card could not have until the figure was real.
    ///
    /// <para>Drawn from an allowance alone it would have been a bar that is always empty — the
    /// reason the sidebar's card deliberately had none — and it turns amber at the same percentage
    /// the storage bar does, because it is the same <c>PlanMeter</c>.</para>
    /// </summary>
    public double TrafficPercent { get; }

    public string TrafficFillClass { get; }

    public bool HasLinks { get; }

    public string LiveLinksText { get; }

    public string DownloadsAllTimeText { get; }

    public string TrashText { get; }

    public bool HasTrash { get; }

    public string TrashSummary { get; }

    public IReadOnlyList<RecentUploadRowViewModel> RecentUploads { get; }

    public IReadOnlyList<BusyLinkRowViewModel> BusiestLinks { get; }
}

/// <summary>
/// One account card on the operator's dashboard: the comp's card, with the half of it that nothing
/// meters left out rather than drawn empty.
/// </summary>
public sealed record PoolAccountCardViewModel(
    Guid Id,
    string Email,
    string Label,
    string StatusText,
    string StatusBadgeClass,
    string UsedText,
    double UsedPercent,
    string UsedFillClass,
    bool HasQuota)
{
    public static PoolAccountCardViewModel From(PoolAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var percent = PlanMeter.Percent(account.QuotaUsedBytes, account.QuotaTotalBytes);

        return new PoolAccountCardViewModel(
            account.Id,
            account.Email,
            account.Label,
            account.Status switch
            {
                GoogleAccountStatus.Healthy => UiText.Accounts.StatusHealthy,
                GoogleAccountStatus.Paused => UiText.Accounts.StatusPaused,
                _ => UiText.Accounts.StatusDisconnected,
            },
            account.Status switch
            {
                GoogleAccountStatus.Healthy => "badge",
                GoogleAccountStatus.Paused => "badge badge--warn",
                _ => "badge badge--danger",
            },
            UiText.Plans.OfCap(
                DisplayFormats.Bytes(account.QuotaUsedBytes),
                DisplayFormats.Bytes(account.QuotaTotalBytes)),
            percent,
            PlanMeter.FillClass(percent),

            // An account whose quota has never been read has a total of zero, and PlanMeter reads a
            // cap of zero as full. That is right for a plan — being over a cap of nothing is being
            // over it — and wrong here, where it would draw a brand-new account as a red bar at
            // 100%. So the bar is absent instead, which is the shell's own rule for a figure that
            // has not been read yet.
            account.QuotaTotalBytes > 0);
    }
}

/// <summary>One row of «نزدیک به سقف»: a workspace, and how much of its cap is gone.</summary>
public sealed record WorkspacePressureRowViewModel(
    Guid TenantId,
    string Name,
    string UsedText,
    string PercentText,
    double Percent,
    string FillClass)
{
    public static WorkspacePressureRowViewModel From(WorkspacePressure workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var percent = PlanMeter.Percent(workspace.StorageUsedBytes, workspace.StorageQuotaBytes);

        return new WorkspacePressureRowViewModel(
            workspace.TenantId,
            workspace.Name,
            UiText.Plans.OfCap(
                DisplayFormats.Bytes(workspace.StorageUsedBytes),
                DisplayFormats.Bytes(workspace.StorageQuotaBytes)),
            Numerals.Percent(percent),
            percent,
            PlanMeter.FillClass(percent));
    }
}

/// <summary>
/// One failed upload, as the «کارهای ناموفق» card reads it.
/// </summary>
/// <param name="Reason">
/// The words the failure arrived in, untranslated, or the sentence that says none were recorded.
/// Rendered on this screen and no other: it can carry a resumable session URI or the address of a
/// Google account, and both are the operator's business alone.
/// </param>
public sealed record FailedTransferRowViewModel(
    string FileName,
    string SizeText,
    string WhenText,
    string Reason,
    bool HasReason)
{
    public static FailedTransferRowViewModel From(FailedTransfer failure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new FailedTransferRowViewModel(
            failure.FileName,
            DisplayFormats.Bytes(failure.SizeBytes),
            DisplayFormats.Relative(failure.FailedAt, now),
            failure.Reason ?? UiText.Dashboard.NoReasonRecorded,
            failure.Reason is { Length: > 0 });
    }
}

/// <summary>
/// One column of the egress chart: a day, what was served on it, and how tall that makes it.
/// </summary>
/// <param name="Percent">
/// The day's bytes as a share of the <b>busiest</b> day in the window, not of any ceiling.
///
/// <para>There is no ceiling to draw against. What a plan sells is per workspace and this chart is
/// every workspace at once; what the box's uplink can do is a bandwidth figure nobody has measured.
/// A bar against an invented denominator would be the operator's home page quietly asserting a limit
/// that does not exist — so the tallest column is full height and the rest are read against it.</para>
/// </param>
/// <param name="Title">
/// The column's own label, for a tooltip and for anything reading the markup rather than the
/// picture. Every column carries one, including the empty days: «nothing on the 14th» is a fact, and
/// a column with no label is one a reader cannot ask about.
/// </param>
public sealed record EgressDayViewModel(string Title, double Percent);

/// <summary>
/// «داشبورد» as the operator's screen renders it: the pool, who is running out, and what is broken.
///
/// <para>Every figure on it is counted from rows that exist. The one the comp draws and this product
/// still does not meter — each account's daily upload allowance — is absent and said in words,
/// because a bar drawn from nothing is a bar that is always empty and an operator reading it would
/// conclude the pool is idle on the day it is not. The egress chart used to be in that sentence with
/// it; <c>ITrafficMeter</c> counts those bytes, so it is drawn.</para>
/// </summary>
public sealed class OperatorDashboardPageViewModel
{
    public OperatorDashboardPageViewModel(OperatorDashboard dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        Accounts = [.. dashboard.Accounts.Select(PoolAccountCardViewModel.From)];

        HasAccounts = dashboard.Accounts.Count > 0;
        ConnectedAccountsText = UiText.Dashboard.PoolAccounts(
            dashboard.Accounts.Count - dashboard.DisconnectedAccountCount);

        DisconnectedText = dashboard.DisconnectedAccountCount > 0
            ? UiText.Dashboard.AccountsDisconnected(dashboard.DisconnectedAccountCount)
            : null;

        PoolUsedText = UiText.Plans.OfCap(
            DisplayFormats.Bytes(dashboard.PoolUsedBytes),
            DisplayFormats.Bytes(dashboard.PoolTotalBytes));

        PoolPercent = PlanMeter.Percent(dashboard.PoolUsedBytes, dashboard.PoolTotalBytes);
        PoolFillClass = PlanMeter.FillClass(PoolPercent);
        HasPool = dashboard.PoolTotalBytes > 0;

        // The same sentence the operator's plan screen prints, from the same two quantities. Over-
        // commitment is shown rather than prevented — caps are ceilings, not reservations — and the
        // note beside it is the one that says so.
        CommittedText = UiText.Plans.Committed(
            DisplayFormats.Bytes(dashboard.CommittedStorageBytes),
            DisplayFormats.Bytes(dashboard.PoolTotalBytes));

        IsOverCommitted = dashboard.IsOverCommitted;

        WorkspaceCountText = UiText.Dashboard.WorkspaceCount(dashboard.WorkspaceCount);
        HasWorkspaces = dashboard.WorkspaceCount > 0;

        Pressing = [.. dashboard.WorkspacesNearTheirCeiling.Select(WorkspacePressureRowViewModel.From)];
        CeilingNote = UiText.Dashboard.WorkspacesNote(dashboard.NearCeilingPercent);

        InFlightText = Numerals.Count(dashboard.TransfersInFlight);
        FailedText = Numerals.Count(dashboard.TransfersFailedInWindow);
        FailedLabel = UiText.Dashboard.TransfersFailedLabel(dashboard.FailureWindowHours);
        HasFailures = dashboard.TransfersFailedInWindow > 0;

        var now = DateTimeOffset.UtcNow;

        Failures = [.. dashboard.RecentFailures.Select(failure => FailedTransferRowViewModel.From(failure, now))];

        // The card shows a handful and says how many it is not showing, so the list stays bounded
        // and the figure above it stays the truth.
        var hidden = dashboard.TransfersFailedInWindow - Failures.Count;
        MoreFailuresText = hidden > 0 ? UiText.Dashboard.MoreFailures(hidden) : null;

        // ── the egress chart ────────────────────────────────────────────────────────────────────
        //
        // Every column's height is worked out here rather than in the view, for the reason the rest
        // of this file gives: arithmetic in a view is arithmetic no test can reach. What the view
        // gets is a percentage and a sentence per column.
        var peak = dashboard.EgressPeakDayBytes;

        EgressWindowText = UiText.Dashboard.EgressWindow(dashboard.EgressWindowDays);
        EgressTotalText = UiText.Dashboard.EgressTotal(DisplayFormats.Bytes(dashboard.EgressWindowBytes));
        EgressPeakText = UiText.Dashboard.EgressPeak(DisplayFormats.Bytes(peak));

        // False on a product whose links have served nothing in the window — a new deployment, and a
        // quiet fortnight. Thirty empty columns say «zero» far less clearly than the sentence does,
        // and the sentence cannot be misread as a chart that failed to load.
        HasEgress = peak > 0;

        EgressChartLabel = UiText.Dashboard.EgressChartLabel(
            dashboard.EgressWindowDays,
            DisplayFormats.Bytes(dashboard.EgressWindowBytes));

        Egress =
        [
            .. dashboard.EgressByDay.Select(day => new EgressDayViewModel(
                UiText.Dashboard.EgressDay(
                    DisplayFormats.PanelDate(day.Day),
                    DisplayFormats.Bytes(day.EgressBytes)),

                // Against the busiest day, because there is no ceiling here to draw against — see
                // EgressDayViewModel.Percent. A day with traffic on it never rounds to nothing: two
                // percent is the floor, so «a little» and «none at all» stay two different pictures
                // rather than two identical empty columns. It is the one place this chart is not
                // linear, and it is deliberately at the bottom where it cannot flatter a spike.
                day.EgressBytes <= 0 || peak <= 0
                    ? 0d
                    : Math.Max(2d, Math.Round(day.EgressBytes * 100d / peak, 2)))),
        ];

        EgressFromText = dashboard.EgressByDay.Count == 0
            ? string.Empty
            : DisplayFormats.PanelDate(dashboard.EgressByDay[0].Day);

        EgressToText = dashboard.EgressByDay.Count == 0
            ? string.Empty
            : DisplayFormats.PanelDate(dashboard.EgressByDay[^1].Day);
    }

    public IReadOnlyList<PoolAccountCardViewModel> Accounts { get; }

    public bool HasAccounts { get; }

    public string ConnectedAccountsText { get; }

    public string? DisconnectedText { get; }

    public string PoolUsedText { get; }

    public double PoolPercent { get; }

    public string PoolFillClass { get; }

    public bool HasPool { get; }

    public string CommittedText { get; }

    public bool IsOverCommitted { get; }

    public string WorkspaceCountText { get; }

    public bool HasWorkspaces { get; }

    public IReadOnlyList<WorkspacePressureRowViewModel> Pressing { get; }

    /// <summary>The threshold the reader selected the list with, in the sentence under it.</summary>
    public string CeilingNote { get; }

    public string InFlightText { get; }

    public string FailedText { get; }

    public string FailedLabel { get; }

    public bool HasFailures { get; }

    public IReadOnlyList<FailedTransferRowViewModel> Failures { get; }

    public string? MoreFailuresText { get; }

    /// <summary>One entry per day of the window, oldest first, with the quiet days present and flat.</summary>
    public IReadOnlyList<EgressDayViewModel> Egress { get; }

    /// <summary>
    /// False when nothing was served in the window at all, which is a sentence rather than thirty
    /// empty columns — an empty chart reads as one that failed to load.
    /// </summary>
    public bool HasEgress { get; }

    public string EgressWindowText { get; }

    public string EgressTotalText { get; }

    /// <summary>The tallest column, named: it is the height every other one is drawn against.</summary>
    public string EgressPeakText { get; }

    /// <summary>The chart said in a sentence, for a reader who is not looking at the picture.</summary>
    public string EgressChartLabel { get; }

    /// <summary>The oldest day in the window — the axis label at the start of the run.</summary>
    public string EgressFromText { get; }

    /// <summary>…and the newest, at the end of it. Empty strings when the window holds nothing.</summary>
    public string EgressToText { get; }
}
