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

/// <summary>
/// One tier on the operator's catalogue table, with the two figures that decide what its buttons
/// may do.
/// </summary>
/// <param name="WorkspacesOnPlan">
/// How many workspaces carry this tier. <b>None of them moves when the tier is edited</b> — the
/// figure is the size of a re-apply, not the size of an edit, and it is also what makes delete
/// impossible: <c>Tenant.PlanId</c> is a <c>Restrict</c> foreign key.
/// </param>
public sealed record PlanCatalogueRowViewModel(
    string Code,
    string Name,
    string StorageText,
    string MaxFileText,
    string MonthlyTrafficText,
    string SeatsText,
    string StatusText,
    bool IsRetired,
    string WorkspacesText,
    bool IsConfiguredDefault)
{
    public static PlanCatalogueRowViewModel From(PlanUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new PlanCatalogueRowViewModel(
            usage.Plan.Code,
            usage.Plan.Name,
            DisplayFormats.Bytes(usage.Plan.Numbers.StorageBytes),
            DisplayFormats.Bytes(usage.Plan.Numbers.MaxFileBytes),
            DisplayFormats.Bytes(usage.Plan.Numbers.MonthlyEgressBytes),
            Numerals.Count(usage.Plan.Numbers.MaxMembers),
            usage.Plan.IsRetired ? UiText.Plans.StatusRetired : UiText.Plans.StatusLive,
            usage.Plan.IsRetired,
            Numerals.Count(usage.WorkspacesOnPlan),
            usage.IsConfiguredDefault);
    }
}

/// <summary>The operator's plan screen: the catalogue, every workspace, and the commitment.</summary>
public sealed class OperatorPlansPageViewModel(
    PlanCatalogueState catalogue,
    OperatorPlanOverview overview)
{
    public IReadOnlyList<PlanCatalogueRowViewModel> Plans { get; } =
        [.. catalogue.Tiers.Select(PlanCatalogueRowViewModel.From)];

    /// <summary>
    /// Null while <c>Plans:DefaultPlanCode</c> names a row that exists. When it does not, every
    /// sign-up throws and nothing else on this screen would say so — the setting is validated at
    /// start-up only for emptiness, because checking the rest needs a database.
    /// </summary>
    public string? DefaultMissingText { get; } = catalogue.DefaultPlanExists
        ? null
        : UiText.PlanAdmin.DefaultMissingBody(catalogue.DefaultPlanCode);

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
/// A tier's code, name and four numbers as the catalogue form posts them.
///
/// <para><b>The three ceilings are in GB and nothing else on the way through scales them.</b>
/// <c>DisplayFormats.Bytes</c> renders 6 TiB as <c>6 TB</c>, so a form pre-filled from it would show
/// <c>6</c> in a field labelled GB and divide the tier by 1024 on the next save. See
/// <see cref="PlanSize"/> for the binary-versus-decimal decision this follows rather than invents.</para>
///
/// <para>An explicit request type, never the entity. <c>Id</c>, <c>SortOrder</c>, <c>IsRetired</c>
/// and <c>CreatedAt</c> are on no shape a request can bind to, so an over-posted field has nothing
/// to land on: order is moved by its own command and retirement is its own command.</para>
/// </summary>
public sealed class PlanForm
{
    [Required]
    [StringLength(32)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    public long StorageGb { get; set; }

    public long MaxFileGb { get; set; }

    public long TrafficGb { get; set; }

    public int Seats { get; set; }

    /// <summary>
    /// The form's figures as a tier, refusing anything the multiplication could not survive.
    ///
    /// <para>The range is checked <i>before</i> the multiply rather than after it: a gigabyte figure
    /// with four extra zeros in it overflows a <c>long</c> and wraps, and a wrapped ceiling can land
    /// on a plausible-looking number instead of an obviously wrong one.</para>
    /// </summary>
    public PlanDraft ToDraft()
    {
        foreach (var gigabytes in new[] { StorageGb, MaxFileGb, TrafficGb })
        {
            if (!PlanSize.IsInRange(gigabytes))
            {
                throw new PlanEditRefusedException(
                    PlanEditRefusal.NumberOutOfRange,
                    $"{gigabytes} GB is outside what a tier can hold.");
            }
        }

        return new PlanDraft(
            Code,
            Name,
            new PlanNumbers(
                StorageBytes: PlanSize.ToBytes(StorageGb),
                MaxFileBytes: PlanSize.ToBytes(MaxFileGb),
                MonthlyEgressBytes: PlanSize.ToBytes(TrafficGb),
                MaxMembers: Seats));
    }

    /// <summary>The form as it opens on an existing tier: one unit in, the same unit back out.</summary>
    public static PlanForm From(PlanSummary plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new PlanForm
        {
            Code = plan.Code,
            Name = plan.Name,
            StorageGb = PlanSize.ToGigabytes(plan.Numbers.StorageBytes),
            MaxFileGb = PlanSize.ToGigabytes(plan.Numbers.MaxFileBytes),
            TrafficGb = PlanSize.ToGigabytes(plan.Numbers.MonthlyEgressBytes),
            Seats = plan.Numbers.MaxMembers,
        };
    }

    /// <summary>
    /// What a blank create form starts with: the smallest seeded tier's shape.
    ///
    /// <para>Not blank boxes. Four empty number inputs post four zeros, and a zero ceiling is the
    /// one value that refuses every upload on the tier — so the form would open on the most
    /// dangerous figure it can hold and rely on a refusal to catch it.</para>
    /// </summary>
    public static PlanForm Blank() => new()
    {
        StorageGb = PlanSize.ToGigabytes(PlanCatalogue.Default.StorageBytes),
        MaxFileGb = PlanSize.ToGigabytes(PlanCatalogue.Default.MaxFileBytes),
        TrafficGb = PlanSize.ToGigabytes(PlanCatalogue.Default.MonthlyEgressBytes),
        Seats = PlanCatalogue.Default.MaxMembers,
    };
}

/// <summary>
/// The create and edit screen for one tier.
///
/// <para>Its largest job is not collecting six values. It is saying, where the operator is typing,
/// that saving moves nobody — the plan is a template whose numbers were copied onto each workspace
/// when it was applied, and an operator who edits «پایه» and assumes every Starter customer just
/// moved is the single most likely misunderstanding this screen can cause.</para>
/// </summary>
public sealed class PlanFormPageViewModel
{
    private PlanFormPageViewModel(PlanForm values, PlanUsage? usage, string? error)
    {
        Values = values;
        Error = error;

        IsNew = usage is null;
        Code = usage?.Plan.Code;
        Title = usage is null ? UiText.PlanAdmin.NewTier : UiText.PlanAdmin.EditTitle(usage.Plan.Name);
        WorkspacesOnPlan = usage?.WorkspacesOnPlan ?? 0;
        WorkspacesHoldingOtherNumbers = usage?.WorkspacesHoldingOtherNumbers ?? 0;
        IsConfiguredDefault = usage?.IsConfiguredDefault ?? false;
        IsRetired = usage?.Plan.IsRetired ?? false;

        MovesNobodyText = WorkspacesOnPlan == 0
            ? UiText.PlanAdmin.MovesNobodyOnAnEmptyTier
            : UiText.PlanAdmin.MovesNobodyBody(WorkspacesOnPlan);

        // Only reachable for a row that was not written through this form — a hand-edited seed, a
        // support insert. Saying so beats rounding somebody's tier because they opened the screen.
        var numbers = usage?.Plan.Numbers;
        RoundedFromBytes = numbers is { } exact && !(
            PlanSize.IsWholeGigabytes(exact.StorageBytes)
            && PlanSize.IsWholeGigabytes(exact.MaxFileBytes)
            && PlanSize.IsWholeGigabytes(exact.MonthlyEgressBytes));

        StoredExactlyText = numbers is { } stored
            ? UiText.PlanAdmin.StoredExactly(string.Join(
                " · ",
                DisplayFormats.Bytes(stored.StorageBytes),
                DisplayFormats.Bytes(stored.MaxFileBytes),
                DisplayFormats.Bytes(stored.MonthlyEgressBytes)))
            : null;

        // Per-file is the error bar on the traffic allowance — the invariant PlanCatalogue argues
        // for and PlanTemplateTests pins on the seeded tiers. A warning rather than a refusal: the
        // numbers are the owner's, and an unwarned tier is one a customer screenshots.
        FileIsLargeAgainstTraffic =
            values.MaxFileGb > 0 && values.MaxFileGb * 200 >= values.TrafficGb;
    }

    public bool IsNew { get; }

    /// <summary>Null for a tier that does not exist yet, which is what the form posts to.</summary>
    public string? Code { get; }

    public string Title { get; }

    public PlanForm Values { get; }

    /// <summary>Already a sentence, in this request's language. Null when nothing was refused.</summary>
    public string? Error { get; }

    public int WorkspacesOnPlan { get; }

    public int WorkspacesHoldingOtherNumbers { get; }

    /// <summary>
    /// The tier <c>Plans:DefaultPlanCode</c> names. Its code is not editable here: the setting is a
    /// string in a file that nothing reconciles, and re-coding the row it names turns the next
    /// sign-up into a 500.
    /// </summary>
    public bool IsConfiguredDefault { get; }

    public string MovesNobodyText { get; }

    public bool RoundedFromBytes { get; }

    public string? StoredExactlyText { get; }

    public bool FileIsLargeAgainstTraffic { get; }

    public bool IsRetired { get; }

    /// <summary>Offered only where there is somebody to re-apply to.</summary>
    public bool CanReapply => !IsNew && WorkspacesOnPlan > 0;

    /// <summary>
    /// The default is what every new workspace is created on, so it is never taken off sale from
    /// here. Changing which tier that is means changing <c>Plans:DefaultPlanCode</c>.
    /// </summary>
    public bool CanBeRetired => !IsNew && !IsConfiguredDefault;

    /// <summary>
    /// Drawn only for a tier nobody is on and nothing hands out. A button whose only outcome is a
    /// refusal teaches an operator to distrust the rest of the screen.
    /// </summary>
    public bool CanBeDeleted => !IsNew && WorkspacesOnPlan == 0 && !IsConfiguredDefault;

    public static PlanFormPageViewModel ForNew(PlanForm values, string? error = null) =>
        new(values, usage: null, error);

    public static PlanFormPageViewModel ForEdit(PlanUsage usage, PlanForm values, string? error = null) =>
        new(values, usage, error);
}

/// <summary>
/// Which way the retirement switch was thrown.
///
/// <para>The state is posted rather than toggled from whatever the row currently holds: a toggle
/// read from the server means two operators on the same list undo each other, and the second one
/// sees a tier come back on sale without having asked for it.</para>
/// </summary>
public sealed class RetireTierForm
{
    public bool Retired { get; set; }
}

/// <summary>One step up or down the list. Not a sort order — the operator does not type a number.</summary>
public sealed class MoveTierForm
{
    public PlanMove Direction { get; set; }
}

/// <summary>The reason a bulk re-apply gives every workspace it touches. Required, like every other quota change.</summary>
public sealed class ReapplyPlanForm
{
    [Required]
    [StringLength(512)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The confirmation in front of the one catalogue action that reaches customers.
///
/// <para>It is a screen of its own rather than a button on the edit form, because what it does is
/// the opposite of what that form does: the edit moves nobody, and this moves everybody on the
/// tier — including a customer carrying a negotiated ceiling somebody sold them.</para>
/// </summary>
public sealed class ReapplyPlanPageViewModel(PlanUsage usage)
{
    public string Code { get; } = usage.Plan.Code;

    public string Title { get; } = UiText.PlanAdmin.ReapplyTitle(usage.Plan.Name);

    public int WorkspacesOnPlan { get; } = usage.WorkspacesOnPlan;

    public string CountsText { get; } = usage.WorkspacesOnPlan == 0
        ? UiText.PlanAdmin.ReapplyOnAnEmptyTier
        : UiText.PlanAdmin.ReapplyCounts(usage.WorkspacesOnPlan, usage.WorkspacesHoldingOtherNumbers);

    public string StorageText { get; } = DisplayFormats.Bytes(usage.Plan.Numbers.StorageBytes);

    public string MaxFileText { get; } = DisplayFormats.Bytes(usage.Plan.Numbers.MaxFileBytes);

    public string MonthlyTrafficText { get; } = DisplayFormats.Bytes(usage.Plan.Numbers.MonthlyEgressBytes);

    public string SeatsText { get; } = Numerals.Count(usage.Plan.Numbers.MaxMembers);
}

/// <summary>
/// A catalogue refusal, as the sentence a person reads.
///
/// <para>The mapping lives here rather than in the service for the reason the whole catalogue does:
/// a refusal has to be readable in both languages, and a service that carried the wording would have
/// written half a screen in one language somewhere <c>LocalizationCatalogueTests</c> cannot see it.
/// It is the same shape <see cref="QuotaChangeRowViewModel.FieldName"/> already uses for an enum.</para>
/// </summary>
public static class PlanRefusalText
{
    public static string For(PlanEditRefusal reason, string defaultPlanCode) => reason switch
    {
        PlanEditRefusal.NotFound => UiText.Plans.PlanNotFound,
        PlanEditRefusal.CodeMalformed => UiText.PlanAdmin.RefusedCodeMalformed,
        PlanEditRefusal.CodeTaken => UiText.PlanAdmin.RefusedCodeTaken,
        PlanEditRefusal.NameInvalid => UiText.PlanAdmin.RefusedNameInvalid,
        PlanEditRefusal.NumberOutOfRange => UiText.PlanAdmin.RefusedNumberOutOfRange,
        PlanEditRefusal.FileLargerThanStorage => UiText.PlanAdmin.RefusedFileLargerThanStorage,
        PlanEditRefusal.DefaultCannotBeRecoded =>
            UiText.PlanAdmin.RefusedDefaultCannotBeRecoded(defaultPlanCode),
        PlanEditRefusal.DefaultCannotBeRetired =>
            UiText.PlanAdmin.RefusedDefaultCannotBeRetired(defaultPlanCode),
        PlanEditRefusal.DefaultCannotBeDeleted =>
            UiText.PlanAdmin.RefusedDefaultCannotBeDeleted(defaultPlanCode),
        PlanEditRefusal.InUseCannotBeDeleted => UiText.PlanAdmin.RefusedInUseCannotBeDeleted,

        // A reason nobody has written a sentence for yet. It says the change did not happen and
        // names the reason rather than guessing at one of the ten above — a wrong sentence on a
        // refusal is worse than a bare one, because the operator acts on it.
        _ => UiText.Plans.ChangeRefused(reason.ToString()),
    };
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
