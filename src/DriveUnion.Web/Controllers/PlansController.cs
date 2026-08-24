using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// Two screens over one set of numbers: «پلن و مصرف» for the customer whose numbers they are, and
/// the catalogue and cross-tenant usage for the operator who sells them.
///
/// <para><b>They are different route surfaces, not one screen with a flag in it.</b> Everything the
/// operator can do lives under <c>/operator/*</c> behind its own policy, and the customer's action
/// reads <c>IsOperator</c> nowhere at all. A flag that no tenant-facing code consults cannot widen a
/// tenant-facing path.</para>
///
/// <para><b>The operator never gets an implicit tenant.</b> On <c>/operator/plans/tenants/{tenantId}</c>
/// the id comes from the route and is handed to the same tenant-scoped service a customer's own
/// request would call. There is deliberately no unscoped overload and no nullable tenantId meaning
/// "every workspace"; the cross-tenant figures come from a separately named reader that returns
/// aggregates and never a file row.</para>
/// </summary>
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class PlansController(
    ITenantPlanService plans,
    IPlanCatalogueReader catalogue,
    IOperatorPlanReader operatorView) : Controller
{
    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string MessageKey = "PlansMessage";

    private const string ErrorKey = "PlansError";

    /// <summary>
    /// The customer's own card: their four numbers, what they have spent, and — when they are over
    /// their storage cap — what that does and does not mean.
    ///
    /// <para>There is no upgrade button, because there is no checkout for one to lead to. A plan
    /// change is an operator action until money is scoped, and an affordance that goes nowhere is
    /// worse than its absence.</para>
    /// </summary>
    [HttpGet("/plans")]
    [Authorize(Policy = DriveUnionPolicies.Tenant)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var plan = await plans.GetAsync(tenantId, cancellationToken);

        // The claim named a tenant and the row is not there. That is a fault rather than an empty
        // screen: a panel that renders zeroes for a workspace that does not exist is how a broken
        // session reads as a customer with no files.
        if (plan is null) return NotFound();

        return View(new TenantPlanPageViewModel(plan));
    }

    /// <summary>The catalogue, every workspace's usage, and the commitment against the real pool.</summary>
    [HttpGet("/operator/plans")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Operator(CancellationToken cancellationToken)
    {
        SetShell();

        // Retired tiers are included here and excluded from the assignment list on the workspace
        // page: this screen has to be able to explain a workspace that is still on one.
        var tiers = await catalogue.ListAsync(includeRetired: true, cancellationToken);
        var overview = await operatorView.OverviewAsync(cancellationToken);

        return View(new OperatorPlansPageViewModel(tiers, overview));
    }

    /// <summary>
    /// One workspace: its effective numbers, its quota history, and — with <c>?plan=</c> — what
    /// applying a tier would leave it holding, before the operator confirms.
    /// </summary>
    [HttpGet("/operator/plans/tenants/{tenantId:guid}")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> OperatorTenant(
        Guid tenantId,
        string? plan,
        CancellationToken cancellationToken)
    {
        SetShell();

        var view = await plans.GetAsync(tenantId, cancellationToken);
        if (view is null) return NotFound();

        var assignable = await catalogue.ListAsync(includeRetired: false, cancellationToken);
        var history = await plans.HistoryAsync(tenantId, cancellationToken);

        var preview = string.IsNullOrWhiteSpace(plan)
            ? null
            : await plans.PreviewPlanAsync(tenantId, plan, cancellationToken);

        return View(new OperatorTenantPlanPageViewModel(view, assignable, history, preview, plan));
    }

    /// <summary>
    /// Copies a tier's numbers onto the workspace, writing one history row per number that moved.
    ///
    /// <para>A downgrade is the same write as an upgrade. The lower number is stored and the customer
    /// may be over it, because being over a cap is a state the product already has: uploads stop,
    /// nothing is deleted, and the way out is deleting files, which needs the panel, which keeps
    /// working. Refusing the downgrade instead would make the operator's own commercial action
    /// impossible.</para>
    /// </summary>
    [HttpPost("/operator/plans/tenants/{tenantId:guid}")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Apply(
        Guid tenantId,
        [FromForm] ApplyPlanForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Reason))
        {
            TempData[ErrorKey] = UiText.Plans.ReasonRequired;
            return RedirectToAction(nameof(OperatorTenant), new { tenantId, plan = form.PlanCode });
        }

        try
        {
            var applied = await plans.SetTenantPlanAsync(
                tenantId, form.PlanCode, form.Reason, CurrentUserId(), cancellationToken);

            TempData[MessageKey] = UiText.Plans.PlanApplied(applied.PlanName ?? form.PlanCode);
        }
        catch (KeyNotFoundException)
        {
            // One message for "no such workspace" and "no such tier" is not needed here — this is an
            // operator surface and the distinction leaks nothing — so each says which it was.
            TempData[ErrorKey] = UiText.Plans.PlanNotFound;
        }
        catch (InvalidOperationException refusal)
        {
            // A retired tier being assigned to a workspace that is not already on it. The message is
            // the service's own sentence, which names the tier and the rule.
            TempData[ErrorKey] = UiText.Plans.ChangeRefused(refusal.Message);
        }

        return RedirectToAction(nameof(OperatorTenant), new { tenantId });
    }

    /// <summary>
    /// Moves one number on one workspace, leaving it on its tier.
    ///
    /// <para>This is the same command M5 §10 named <c>SetTenantStorageQuota</c>, widened to all four
    /// dimensions rather than joined by three more writers. A negotiated customer is the normal case,
    /// and a product that cannot express one forces the operator to invent a fake tier per customer.</para>
    /// </summary>
    [HttpPost("/operator/plans/tenants/{tenantId:guid}/override")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Override(
        Guid tenantId,
        [FromForm] QuotaOverrideForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Reason))
        {
            TempData[ErrorKey] = UiText.Plans.ReasonRequired;
            return RedirectToAction(nameof(OperatorTenant), new { tenantId });
        }

        try
        {
            await plans.SetTenantQuotaOverrideAsync(
                tenantId, form.Field, form.Value, form.Reason, CurrentUserId(), cancellationToken);

            TempData[MessageKey] = UiText.Plans.OverrideApplied;
        }
        catch (KeyNotFoundException)
        {
            TempData[ErrorKey] = UiText.Plans.TenantNotFound;
        }
        catch (ArgumentOutOfRangeException refusal)
        {
            // A dimension nobody defined, or a seat count that is not a number of people. The
            // comparison in the service is fail-open on garbage, so it refuses rather than matches.
            TempData[ErrorKey] = UiText.Plans.ChangeRefused(refusal.Message);
        }

        return RedirectToAction(nameof(OperatorTenant), new { tenantId });
    }

    /// <summary>
    /// The operator who pressed the button, or null when the principal carries no usable id.
    ///
    /// <para>Null rather than <c>Guid.Empty</c>: an empty id in an audit trail is a person who does
    /// not exist, and the whole value of the trail is that everything in it is true.</para>
    /// </summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : null;

    // The pool's size and its daily quota are operator figures. This sets neither, on either screen:
    // the shell asks the principal whether to draw them, which is the same claim the operator policy
    // authorises on, so there is no per-page flag here that a page could set wrongly.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
