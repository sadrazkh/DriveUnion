using DriveUnion.Core.Application;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>One workspace on the operator's list.</summary>
public sealed record TenantRowViewModel(
    Guid TenantId,
    string Name,
    string Slug,
    string PlanText,
    string MembersText,
    bool SeatsAreFull,
    string StorageText,
    double StoragePercent,
    string StorageFillClass,
    string FilesText,
    string CreatedText)
{
    public static TenantRowViewModel From(TenantListing tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return new TenantRowViewModel(
            tenant.TenantId,
            tenant.Name,
            tenant.Slug,
            tenant.PlanCode ?? UiText.Tenants.NoPlan,
            UiText.Tenants.MembersOfCap(tenant.MemberCount, tenant.MaxMembers),
            tenant.SeatsAreFull,
            UiText.Tenants.OfCap(
                DisplayFormats.Bytes(tenant.StorageUsedBytes),
                DisplayFormats.Bytes(tenant.StorageQuotaBytes)),
            PlanMeter.Percent(tenant.StorageUsedBytes, tenant.StorageQuotaBytes),
            PlanMeter.FillClass(PlanMeter.Percent(tenant.StorageUsedBytes, tenant.StorageQuotaBytes)),
            UiText.Tenants.FileCount(tenant.FileCount),
            DisplayFormats.PanelDateTime(tenant.CreatedAt));
    }
}

/// <summary>
/// «فضاهای کاری»: every workspace, and the form that makes one.
///
/// <para>The form is on the list page rather than at its own address because creating a workspace is
/// four fields and the operator's next action is always to open the one they just made. A second
/// screen between those two would be a page whose only content is a form the list already has room
/// for.</para>
/// </summary>
public sealed class TenantsPageViewModel(
    IReadOnlyList<TenantListing> tenants,
    IReadOnlyList<PlanSummary> assignable,
    string? defaultPlanCode,
    CreateTenantForm form)
{
    public IReadOnlyList<TenantRowViewModel> Tenants { get; } = [.. tenants.Select(TenantRowViewModel.From)];

    public string CountText { get; } = UiText.Tenants.Count(tenants.Count);

    /// <summary>Retired tiers are absent: they are hidden from new assignment, not disabled.</summary>
    public IReadOnlyList<PlanRowViewModel> Assignable { get; } = [.. assignable.Select(PlanRowViewModel.From)];

    public string? DefaultPlanCode { get; } = defaultPlanCode;

    /// <summary>
    /// What the operator typed, rendered back after a refusal. A slug they spent a minute choosing
    /// must not be cleared by a message about the name field.
    /// </summary>
    public CreateTenantForm Form { get; } = form;
}

/// <summary>One person in a workspace, on the operator's screen. Carries no credential of any kind.</summary>
public sealed record TenantMemberRowViewModel(
    Guid UserId,
    string Email,
    string DisplayName,
    bool IsDisabled,
    string StatusText,
    string AddedText)
{
    public static TenantMemberRowViewModel From(TenantMemberListing member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new TenantMemberRowViewModel(
            member.UserId,
            member.Email,

            // An em dash rather than an empty cell: a blank in a table reads as a rendering fault,
            // and a display name is optional on purpose.
            string.IsNullOrWhiteSpace(member.DisplayName) ? "—" : member.DisplayName,
            member.IsDisabled,
            member.IsDisabled ? UiText.Tenants.StatusDisabled : UiText.Tenants.StatusActive,
            DisplayFormats.PanelDateTime(member.CreatedAt));
    }
}

/// <summary>
/// One workspace: who is in it, what it is allowed to spend, and what has been changed about that.
///
/// <para>There is no file list here and there must not be. An operator inspecting a customer's files
/// does it through the same tenant-scoped catalogue the customer's own request calls; a second,
/// operator-flavoured file view would be the one that forgets to scope.</para>
/// </summary>
public sealed class TenantPageViewModel(
    TenantWorkspaceView workspace,
    TenantPlanView plan,
    IReadOnlyList<QuotaChangeEntry> history,
    CreateMemberForm form)
{
    public Guid TenantId { get; } = workspace.TenantId;

    public string Name { get; } = workspace.Name;

    public string Slug { get; } = workspace.Slug;

    public string CreatedText { get; } = DisplayFormats.PanelDateTime(workspace.CreatedAt);

    public string PlanName { get; } = plan.PlanName ?? UiText.Tenants.NoPlan;

    public string StorageText { get; } = UiText.Tenants.OfCap(
        DisplayFormats.Bytes(plan.StorageUsedBytes),
        DisplayFormats.Bytes(plan.Limits.StorageBytes));

    public double StoragePercent { get; } =
        PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes);

    public string StorageFillClass { get; } =
        PlanMeter.FillClass(PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes));

    public string SeatsText { get; } =
        UiText.Tenants.MembersOfCap(workspace.Members.Count, plan.Limits.MaxMembers);

    /// <summary>
    /// Counted from the members actually listed rather than from the plan view's own figure, so the
    /// table and the button that adds to it can never disagree about whether there is room.
    /// </summary>
    public bool SeatsAreFull { get; } = workspace.Members.Count >= plan.Limits.MaxMembers;

    public IReadOnlyList<TenantMemberRowViewModel> Members { get; } =
        [.. workspace.Members.Select(TenantMemberRowViewModel.From)];

    /// <summary>
    /// The same rows the plans screen draws, read here so «چرا سهمیه‌ام عوض شد» is answerable from
    /// the page an operator is already on. It is one workspace's history and not an audit log.
    /// </summary>
    public IReadOnlyList<QuotaChangeRowViewModel> History { get; } =
        [.. history.Select(QuotaChangeRowViewModel.From)];

    public CreateMemberForm Form { get; } = form;
}

/// <summary>
/// Making a workspace.
///
/// <para>An explicit request shape, never an entity. <c>StorageQuotaBytes</c>, <c>MaxMembers</c> and
/// the rest appear on nothing a request can bind to, so an over-posted quota field has nothing to
/// land on — and neither has <c>IsOperator</c>, whose one writer in this codebase is the config
/// seeder.</para>
/// </summary>
public sealed class CreateTenantForm
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    /// <summary>Empty means <c>Plans:DefaultPlanCode</c>.</summary>
    public string? PlanCode { get; set; }
}

/// <summary>
/// Making an account inside a workspace. The tenant is not on it — it comes from the route — and
/// neither is any flag that would make the account anything but an ordinary member of that tenant.
/// </summary>
public sealed class CreateMemberForm
{
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>
    /// Never rendered back into the page, on any path. A refused password echoed into the HTML is a
    /// credential in the page source, in the back-forward cache and in anything between here and the
    /// browser that keeps response bodies — the same rule the first-run setup screen keeps.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>Setting a password on an existing account. One field, and it goes nowhere else.</summary>
public sealed class ResetMemberPasswordForm
{
    public string Password { get; set; } = string.Empty;
}
