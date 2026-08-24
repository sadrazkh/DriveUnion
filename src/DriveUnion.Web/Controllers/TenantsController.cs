using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// How a customer comes to exist, and how the people inside them do.
///
/// <para>Before this screen the product could not onboard anybody: every <c>new Tenant</c> and every
/// account creation in <c>src/</c> lived under <c>Infrastructure/Seeding</c>, so the only way to add
/// a customer was to set four environment variables and redeploy. That is what this replaces, for
/// everything after the first operator.</para>
///
/// <para><b>Operator authority is a route surface, not a flag tested inside tenant code.</b> Every
/// action here is under <c>/operator/*</c> behind the operator policy, and no tenant-facing
/// controller reads <c>IsOperator</c> at all. A customer who types one of these addresses is refused
/// by the authorisation middleware before any filter runs — not shown a page with the buttons
/// missing, because a hidden control is not an access control.</para>
///
/// <para><b>The operator never gets an implicit tenant.</b> Every write below takes the tenant from
/// the route and hands it to a method whose signature will not compile without it. There is no
/// unscoped overload and no nullable tenantId meaning "every workspace" — the same rule M1 §8 spent
/// its argument on, and the reason a mistyped user id reaches nobody rather than the wrong customer.
/// The product has no global query filter and must not acquire one: <c>/d/{slug}</c> is anonymous,
/// a filter would hand it <c>Guid.Empty</c>, and every live link in the product would stop
/// resolving.</para>
///
/// <para><b>There is no delete.</b> Nothing in this schema has a foreign key from a tenant's rows
/// back to <c>Tenants</c> — scoping is an argument, not a relationship — so removing the row would
/// not fail, it would succeed and orphan every file, link, session and account that named it, while
/// the bytes went on sitting inside the operator's Google accounts with nothing left to reach them
/// by. The workspace page says so in words, and offers disabling every member instead, which is the
/// reversible version of the same intent.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Operator)]
[AutoValidateAntiforgeryToken]
public sealed class TenantsController(
    IOperatorTenantDirectory directory,
    ITenantProvisioning provisioning,
    ITenantPlanService plans,
    IPlanCatalogueReader catalogue,
    IOptions<PlansOptions> planOptions) : Controller
{
    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string MessageKey = "TenantsMessage";

    private const string ErrorKey = "TenantsError";

    /// <summary>Every workspace, and the form that makes one.</summary>
    [HttpGet("/operator/tenants")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        return View(await PageAsync(new CreateTenantForm(), cancellationToken));
    }

    /// <summary>
    /// A workspace and the plan it starts on.
    ///
    /// <para>A refusal re-renders this page rather than redirecting: a slug is a permanent choice
    /// somebody spent a minute on, and clearing the form to say "that name is too long" is how an
    /// operator ends up pasting a slug they had not finished thinking about.</para>
    /// </summary>
    [HttpPost("/operator/tenants")]
    public async Task<IActionResult> Create(
        [FromForm] CreateTenantForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SetShell();

        var result = await provisioning.CreateTenantAsync(
            form.Name, form.Slug, form.PlanCode, CurrentUserId(), cancellationToken);

        if (result.Tenant is { } created)
        {
            TempData[MessageKey] = UiText.Tenants.TenantCreated(created.Name);

            // Straight to the workspace, because the next thing an operator does is always create
            // its first account — without one, nobody can sign in and the workspace is inert.
            return RedirectToAction(nameof(Detail), new { tenantId = created.TenantId });
        }

        ViewData[ErrorKey] = result.Refusal switch
        {
            TenantRefusal.NameRequired => UiText.Tenants.NameRequired,
            TenantRefusal.SlugMalformed => UiText.Tenants.SlugMalformed
                + " " + UiText.Tenants.SlugRule(TenantSlug.MinimumLength, TenantSlug.MaximumLength),
            TenantRefusal.SlugTaken => UiText.Tenants.SlugTaken(TenantSlug.Normalise(form.Slug)),
            _ => UiText.Tenants.PlanNotFound,
        };

        return View(nameof(Index), await PageAsync(form, cancellationToken));
    }

    /// <summary>One workspace: its people, its plan, what it has spent, and what has been changed.</summary>
    [HttpGet("/operator/tenants/{tenantId:guid}")]
    public async Task<IActionResult> Detail(Guid tenantId, CancellationToken cancellationToken)
    {
        SetShell();

        return await WorkspaceAsync(tenantId, new CreateMemberForm(), cancellationToken);
    }

    /// <summary>
    /// An account inside the workspace, with a password the operator sets.
    ///
    /// <para>Refused at <c>Tenant.MaxMembers</c> <b>before the account is created</b>. An account
    /// made and then apologised for is an account that can sign in.</para>
    /// </summary>
    [HttpPost("/operator/tenants/{tenantId:guid}/members")]
    public async Task<IActionResult> CreateMember(
        Guid tenantId,
        [FromForm] CreateMemberForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SetShell();

        if (string.IsNullOrWhiteSpace(form.Password))
        {
            // Asked here rather than in the provisioner, because Identity's own answer to an empty
            // password is a list of everything the policy wants — which is true, unhelpful, and not
            // what somebody who simply left the box empty needs to read.
            ViewData[ErrorKey] = UiText.Tenants.PasswordRequired;

            return await WorkspaceAsync(tenantId, form, cancellationToken);
        }

        var result = await provisioning.CreateMemberAsync(
            tenantId, form.Email, form.DisplayName, form.Password, cancellationToken);

        if (result.Refusal == MemberRefusal.None)
        {
            TempData[MessageKey] = UiText.Tenants.MemberCreated(form.Email.Trim());

            return RedirectToAction(nameof(Detail), new { tenantId });
        }

        if (result.Refusal == MemberRefusal.TenantNotFound) return NotFound();

        ViewData[ErrorKey] = result.Refusal switch
        {
            MemberRefusal.EmailRequired => UiText.Tenants.EmailRequired,
            MemberRefusal.SeatsFull => UiText.Tenants.SeatsFull(result.SeatsUsed, result.MaxMembers),
            _ => UiText.Tenants.Refused(string.Join(" ", result.Errors)),
        };

        // The password is deliberately not carried back into the re-rendered form — see
        // CreateMemberForm.Password. The address and the display name are, because retyping those is
        // the annoyance that makes somebody pick a weaker password the second time.
        form.Password = string.Empty;

        return await WorkspaceAsync(tenantId, form, cancellationToken);
    }

    /// <summary>
    /// Takes an account's access away. See <c>TenantProvisioning.DisableMemberAsync</c> for how it
    /// reaches an open session on that session's next request rather than at its next sign-in.
    /// </summary>
    [HttpPost("/operator/tenants/{tenantId:guid}/members/{userId:guid}/disable")]
    public async Task<IActionResult> DisableMember(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var result = await provisioning.DisableMemberAsync(tenantId, userId, cancellationToken);

        return Announce(tenantId, result, UiText.Tenants.MemberDisabled);
    }

    [HttpPost("/operator/tenants/{tenantId:guid}/members/{userId:guid}/enable")]
    public async Task<IActionResult> EnableMember(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var result = await provisioning.EnableMemberAsync(tenantId, userId, cancellationToken);

        return Announce(tenantId, result, UiText.Tenants.MemberEnabled);
    }

    [HttpPost("/operator/tenants/{tenantId:guid}/members/{userId:guid}/password")]
    public async Task<IActionResult> ResetMemberPassword(
        Guid tenantId,
        Guid userId,
        [FromForm] ResetMemberPasswordForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Password))
        {
            TempData[ErrorKey] = UiText.Tenants.PasswordRequired;

            return RedirectToAction(nameof(Detail), new { tenantId });
        }

        var result = await provisioning.ResetMemberPasswordAsync(
            tenantId, userId, form.Password, cancellationToken);

        return Announce(tenantId, result, UiText.Tenants.PasswordWasReset);
    }

    /// <summary>
    /// The page every write redirects back to, with one sentence about what happened.
    ///
    /// <para>A member id that names nobody <i>in this workspace</i> answers 404 rather than 403. A
    /// 403 confirms the id exists, which is a cross-tenant existence oracle — the same reasoning as
    /// the rule that an unknown slug and a revoked slug render the identical card.</para>
    /// </summary>
    private IActionResult Announce(
        Guid tenantId,
        MemberCommandResult result,
        Func<string, string> said)
    {
        switch (result.Refusal)
        {
            case MemberRefusal.None:
                TempData[MessageKey] = said(result.Email ?? string.Empty);
                break;

            case MemberRefusal.MemberNotFound:
                return NotFound();

            default:
                TempData[ErrorKey] = UiText.Tenants.Refused(string.Join(" ", result.Errors));
                break;
        }

        return RedirectToAction(nameof(Detail), new { tenantId });
    }

    private async Task<TenantsPageViewModel> PageAsync(
        CreateTenantForm form,
        CancellationToken cancellationToken)
    {
        var tenants = await directory.ListAsync(cancellationToken);

        // Retired tiers are excluded: retirement hides a plan from new assignment and leaves every
        // workspace already on it working. Offering one here would be offering the only assignment
        // the plan service refuses.
        var assignable = await catalogue.ListAsync(includeRetired: false, cancellationToken);

        return new TenantsPageViewModel(
            tenants, assignable, planOptions.Value.DefaultPlanCode, form);
    }

    private async Task<IActionResult> WorkspaceAsync(
        Guid tenantId,
        CreateMemberForm form,
        CancellationToken cancellationToken)
    {
        var workspace = await directory.GetAsync(tenantId, cancellationToken);
        if (workspace is null) return NotFound();

        // The same tenant-scoped service the customer's own «پلن و مصرف» card calls, with the id
        // from the route. Null here would mean the workspace vanished between two reads in one
        // request, which is a fault rather than an empty card.
        var plan = await plans.GetAsync(tenantId, cancellationToken);
        if (plan is null) return NotFound();

        var history = await plans.HistoryAsync(tenantId, cancellationToken);

        return View(nameof(Detail), new TenantPageViewModel(workspace, plan, history, form));
    }

    /// <summary>
    /// The operator who pressed the button, or null when the principal carries no usable id. Null
    /// rather than <c>Guid.Empty</c>: an empty id in a quota history is a person who does not exist.
    /// </summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : null;

    // The pool's size and its daily quota are operator figures and this sets neither: the shell asks
    // the principal whether to draw them, which is the same claim this controller's policy
    // authorises on, so there is no per-page flag here that a page could set wrongly.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
