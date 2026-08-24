using System.ComponentModel.DataAnnotations;
using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// The colour ladder a usage meter fills with.
///
/// <para>One rule, borrowed rather than invented: <c>--accent</c> below 80%, <c>--warn</c> at or past
/// 80%, <c>--danger</c> at or past 95%. It follows the <i>rounded</i> figure, so what a reader sees is
/// what tripped the colour — a bar that reads «۸۰٪» and is still blue is a bug report.</para>
///
/// <para><c>ShellContext.QuotaFillClass</c> states the same three thresholds for the sidebar's
/// daily-quota bar. It is not called from here because that one is computed from the two gigabyte
/// figures the shell already holds, and these are bytes against a cap; both take their numbers from
/// the same handoff, and if either moves the other has to move with it.</para>
/// </summary>
public static class PlanMeter
{
    public static string FillClass(double percent) => percent switch
    {
        >= 95d => "bar-fill bar-fill--danger",
        >= 80d => "bar-fill bar-fill--warn",
        _ => "bar-fill",
    };

    /// <summary>The rounded percentage the colour follows, clamped to the track.</summary>
    public static double Percent(long used, long cap) =>
        cap <= 0 ? 100d : Math.Clamp(Math.Round(used * 100d / cap), 0d, 100d);
}

/// <summary>
/// «پلن و مصرف» as the customer's own screen renders it.
///
/// <para>Nothing here names another workspace, the pool, a Google account or the operator's
/// commitment. A customer's plan card is about their own four numbers and nothing else.</para>
/// </summary>
public sealed class TenantPlanPageViewModel(TenantPlanView plan)
{
    public string PlanName { get; } = plan.PlanName ?? UiText.Plans.NoPlan;

    public string? AppliedAtText { get; } = plan.PlanAppliedAt is { } applied
        ? UiText.Plans.AppliedAt(DisplayFormats.PanelDateTime(applied))
        : null;

    public string StorageText { get; } = UiText.Plans.OfCap(
        DisplayFormats.Bytes(plan.StorageUsedBytes),
        DisplayFormats.Bytes(plan.Limits.StorageBytes));

    public double StoragePercent { get; } = PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes);

    public string StorageFillClass { get; } =
        PlanMeter.FillClass(PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes));

    public bool IsOverStorage { get; } = plan.IsOverStorage;

    public string MaxFileText { get; } = DisplayFormats.Bytes(plan.Limits.MaxFileBytes);

    /// <summary>
    /// The per-file rule said as the refusal it becomes, in place, before anybody hits it. A limit
    /// that only ever speaks at the moment it refuses is a limit the customer meets as a surprise.
    /// </summary>
    public string MaxFileExplanation { get; } =
        UiText.Plans.RefusedFileTooLarge(DisplayFormats.Bytes(plan.Limits.MaxFileBytes));

    public string MonthlyTrafficText { get; } = DisplayFormats.Bytes(plan.Limits.MonthlyEgressBytes);

    public string MembersText { get; } = UiText.Plans.MembersOfCap(plan.MembersUsed, plan.Limits.MaxMembers);

    public string FileCountText { get; } = UiText.Plans.FileCount(plan.FileCount);
}

/// <summary>One tier on the operator's catalogue table.</summary>
public sealed record PlanRowViewModel(
    string Code,
    string Name,
    string StorageText,
    string MaxFileText,
    string MonthlyTrafficText,
    string SeatsText,
    string StatusText,
    bool IsRetired)
{
    public static PlanRowViewModel From(PlanSummary plan) => new(
        plan.Code,
        plan.Name,
        DisplayFormats.Bytes(plan.Numbers.StorageBytes),
        DisplayFormats.Bytes(plan.Numbers.MaxFileBytes),
        DisplayFormats.Bytes(plan.Numbers.MonthlyEgressBytes),
        Numerals.Count(plan.Numbers.MaxMembers),
        plan.IsRetired ? UiText.Plans.StatusRetired : UiText.Plans.StatusLive,
        plan.IsRetired);
}

/// <summary>One workspace on the operator's usage table. Aggregates, and no file ever.</summary>
public sealed record OperatorTenantRowViewModel(
    Guid TenantId,
    string Name,
    string PlanText,
    string UsedText,
    double Percent,
    string FillClass,
    string FilesText,
    string MembersText)
{
    public static OperatorTenantRowViewModel From(OperatorTenantPlanRow row) => new(
        row.TenantId,
        row.TenantName,
        row.PlanCode ?? UiText.Plans.NoPlan,
        UiText.Plans.OfCap(
            DisplayFormats.Bytes(row.StorageUsedBytes),
            DisplayFormats.Bytes(row.Limits.StorageBytes)),
        PlanMeter.Percent(row.StorageUsedBytes, row.Limits.StorageBytes),
        PlanMeter.FillClass(PlanMeter.Percent(row.StorageUsedBytes, row.Limits.StorageBytes)),
        Numerals.Count(row.FileCount),
        UiText.Plans.MembersOfCap(row.MembersUsed, row.Limits.MaxMembers));
}

/// <summary>The operator's plan screen: the catalogue, every workspace, and the commitment.</summary>
public sealed class OperatorPlansPageViewModel(
    IReadOnlyList<PlanSummary> catalogue,
    OperatorPlanOverview overview)
{
    public IReadOnlyList<PlanRowViewModel> Plans { get; } = [.. catalogue.Select(PlanRowViewModel.From)];

    public IReadOnlyList<OperatorTenantRowViewModel> Tenants { get; } =
        [.. overview.Tenants.Select(OperatorTenantRowViewModel.From)];

    /// <summary>
    /// «تعهدشده: ۱۴ TB از ۱۰ TB». Over-commitment is displayed rather than prevented, so this line
    /// exists to be over sometimes.
    /// </summary>
    public string CommittedText { get; } = UiText.Plans.Committed(
        DisplayFormats.Bytes(overview.CommittedStorageBytes),
        DisplayFormats.Bytes(overview.PoolStorageBytes));

    public bool IsOverCommitted { get; } = overview.IsOverCommitted;

    public string SoldTrafficText { get; } =
        UiText.Plans.SoldTraffic(DisplayFormats.Bytes(overview.SoldMonthlyEgressBytes));
}

/// <summary>One row of a workspace's quota history.</summary>
public sealed record QuotaChangeRowViewModel(
    string WhenText,
    string FieldText,
    string FromText,
    string ToText,
    string Reason)
{
    public static QuotaChangeRowViewModel From(QuotaChangeEntry entry) => new(
        DisplayFormats.PanelDateTime(entry.ChangedAt),
        FieldName(entry.Field),
        Value(entry.Field, entry.OldValue),
        Value(entry.Field, entry.NewValue),
        entry.Reason);

    public static string FieldName(QuotaField field) => field switch
    {
        QuotaField.StorageBytes => UiText.Plans.FieldStorage,
        QuotaField.MaxFileBytes => UiText.Plans.FieldMaxFile,
        QuotaField.MonthlyEgressBytes => UiText.Plans.FieldTraffic,
        QuotaField.MaxMembers => UiText.Plans.FieldMembers,

        // A value nobody defined is rendered as itself rather than guessed at. Falling through to
        // one of the four would put a wrong label on a real change in the one table that exists to
        // be believed.
        _ => field.ToString(),
    };

    /// <summary>
    /// Three of the four dimensions are byte quantities and one is a number of people, and rendering
    /// «۳» as <c>3 B</c> is how a seat change becomes unreadable.
    /// </summary>
    private static string Value(QuotaField field, long value) => field == QuotaField.MaxMembers
        ? Numerals.Count(value)
        : DisplayFormats.Bytes(value);
}

/// <summary>What applying a plan would leave the workspace holding, shown before it is confirmed.</summary>
public sealed class DowngradePreviewViewModel(DowngradePreview preview)
{
    public string PlanNameText { get; } = UiText.Plans.PreviewHeading;

    public bool Fits { get; } = !preview.ProducesAnOverage;

    public string? StorageOverageText { get; } = preview.StorageOverageBytes > 0
        ? UiText.Plans.PreviewStorageOverage(DisplayFormats.Bytes(preview.StorageOverageBytes))
        : null;

    public string? FilesOverText { get; } = preview.FilesOverNewFileLimit > 0
        ? UiText.Plans.PreviewFilesOver(
            preview.FilesOverNewFileLimit,
            DisplayFormats.Bytes(preview.Proposed.MaxFileBytes))
        : null;

    public string? MembersOverText { get; } = preview.MembersOverNewSeatLimit > 0
        ? UiText.Plans.PreviewMembersOver(preview.MembersOverNewSeatLimit)
        : null;

    public string ProposedStorageText { get; } = DisplayFormats.Bytes(preview.Proposed.StorageBytes);

    public string ProposedFileText { get; } = DisplayFormats.Bytes(preview.Proposed.MaxFileBytes);

    public string ProposedTrafficText { get; } = DisplayFormats.Bytes(preview.Proposed.MonthlyEgressBytes);

    public string ProposedSeatsText { get; } = Numerals.Count(preview.Proposed.MaxMembers);
}

/// <summary>One workspace's plan page on the operator's side.</summary>
public sealed class OperatorTenantPlanPageViewModel(
    TenantPlanView plan,
    IReadOnlyList<PlanSummary> assignable,
    IReadOnlyList<QuotaChangeEntry> history,
    DowngradePreview? preview,
    string? selectedPlanCode)
{
    public Guid TenantId { get; } = plan.TenantId;

    public string TenantName { get; } = plan.TenantName;

    public string PlanName { get; } = plan.PlanName ?? UiText.Plans.NoPlan;

    public string StorageText { get; } = UiText.Plans.OfCap(
        DisplayFormats.Bytes(plan.StorageUsedBytes),
        DisplayFormats.Bytes(plan.Limits.StorageBytes));

    public string MaxFileText { get; } = DisplayFormats.Bytes(plan.Limits.MaxFileBytes);

    public string MonthlyTrafficText { get; } = DisplayFormats.Bytes(plan.Limits.MonthlyEgressBytes);

    public string MembersText { get; } = UiText.Plans.MembersOfCap(plan.MembersUsed, plan.Limits.MaxMembers);

    /// <summary>Retired tiers are absent: they are hidden from new assignment, not disabled.</summary>
    public IReadOnlyList<PlanRowViewModel> Assignable { get; } =
        [.. assignable.Select(PlanRowViewModel.From)];

    public IReadOnlyList<QuotaChangeRowViewModel> History { get; } =
        [.. history.Select(QuotaChangeRowViewModel.From)];

    public DowngradePreviewViewModel? Preview { get; } =
        preview is null ? null : new DowngradePreviewViewModel(preview);

    public string? SelectedPlanCode { get; } = selectedPlanCode;

    public IReadOnlyList<(QuotaField Field, string Label)> Fields { get; } =
    [
        (QuotaField.StorageBytes, UiText.Plans.FieldStorage),
        (QuotaField.MaxFileBytes, UiText.Plans.FieldMaxFile),
        (QuotaField.MonthlyEgressBytes, UiText.Plans.FieldTraffic),
        (QuotaField.MaxMembers, UiText.Plans.FieldMembers),
    ];
}

/// <summary>
/// Applying a plan to one workspace.
///
/// <para>An explicit request type, never an entity: <c>StorageQuotaBytes</c>, <c>MaxFileBytes</c> and
/// the rest appear on no shape a request can bind to, so an over-posted field has nothing to land
/// on. The tenant is not on it either — it comes from the route.</para>
/// </summary>
public sealed class ApplyPlanForm
{
    [Required]
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>
    /// Required, because the history row is the whole reason this command is audited and a change
    /// with no reason is the one a support conversation cannot use.
    /// </summary>
    [Required]
    [StringLength(512)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Moving one number on one workspace, without taking it off its tier.</summary>
public sealed class QuotaOverrideForm
{
    public QuotaField Field { get; set; }

    [Range(0, long.MaxValue)]
    public long Value { get; set; }

    [Required]
    [StringLength(512)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The bodies a plan refusal answers <c>/api/*</c> with.
///
/// <para><b>409, and the <c>limit</c> field is what tells a client which dimension fired.</b> Not 402:
/// it asserts the fix is money when the fix may be waiting or deleting, and this product does not do
/// money at all. Not 429, because nothing here is a rate. Not 507, which proxies retry.</para>
///
/// <para>Only the two shapes P1 can actually produce are here. The traffic and seat bodies belong
/// with the enforcement that raises them — a body no code path can reach is a contract nothing
/// keeps.</para>
/// </summary>
public static class PlanLimitBodies
{
    public sealed record StorageBody(
        string Error,
        string Limit,
        long CapBytes,
        long UsedBytes,
        long RequestedBytes);

    public sealed record FileBody(
        string Error,
        string Limit,
        long MaxFileBytes,
        long RequestedBytes);

    /// <summary>
    /// The body for a refusal, ready for whatever turns an exception into a response.
    ///
    /// <para>It is here rather than at a throw site because the same four strings appear in a body,
    /// a log line and a test, and a fifth spelling of one of them is a client that silently stops
    /// recognising the refusal it was written for.</para>
    /// </summary>
    public static object For(PlanLimitExceededException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return refusal.Limit switch
        {
            PlanLimit.File => new FileBody(
                refusal.Code,
                PlanLimitCodes.Dimension(refusal.Limit),
                refusal.CapBytes,
                refusal.RequestedBytes),

            _ => new StorageBody(
                refusal.Code,
                PlanLimitCodes.Dimension(refusal.Limit),
                refusal.CapBytes,
                refusal.UsedBytes,
                refusal.RequestedBytes),
        };
    }
}
