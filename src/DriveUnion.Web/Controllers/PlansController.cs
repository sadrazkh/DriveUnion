using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
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
    IPlanCatalogueEditor editor,
    IOperatorPlanReader operatorView) : Controller
{
    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string MessageKey = "PlansMessage";

    private const string ErrorKey = "PlansError";

    /// <summary>
    /// One view for creating a tier and for editing one. The two differ in a heading and in whether
    /// the code field is writable; two files would be one file and a copy of it, and the copy is the
    /// one that stops saying the sentence about editing moving nobody.
    /// </summary>
    private const string TierFormView = "Tier";

    private const string ReapplyView = "Reapply";

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
        // page: this screen has to be able to explain a workspace that is still on one, and it is
        // where a retired tier is brought back.
        var state = await editor.StateAsync(cancellationToken);
        var overview = await operatorView.OverviewAsync(cancellationToken);

        return View(new OperatorPlansPageViewModel(state, overview));
    }

    /// <summary>The blank tier form. Nothing is written until it is posted.</summary>
    [HttpGet("/operator/plans/new")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public IActionResult NewTier()
    {
        SetShell();

        return View(TierFormView, PlanFormPageViewModel.ForNew(PlanForm.Blank()));
    }

    /// <summary>
    /// A new tier. It is live and assignable the moment it exists, which costs nothing: nobody is on
    /// it, so nothing can be disturbed by it.
    /// </summary>
    [HttpPost("/operator/plans/new")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Create([FromForm] PlanForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SetShell();

        try
        {
            var created = await editor.CreateAsync(form.ToDraft(), cancellationToken);

            TempData[MessageKey] = UiText.PlanAdmin.TierCreated(created.Name);

            return RedirectToAction(nameof(Tier), new { code = created.Code });
        }
        catch (PlanEditRefusedException refusal)
        {
            // Re-rendered rather than redirected, so six typed values are not lost to one bad one.
            return View(TierFormView, PlanFormPageViewModel.ForNew(form, Sentence(refusal, form.Code)));
        }
    }

    /// <summary>
    /// One tier's form, with the count of workspaces on it and the sentence that says none of them
    /// moves when it is saved.
    /// </summary>
    [HttpGet("/operator/plans/tiers/{code}")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Tier(string code, CancellationToken cancellationToken)
    {
        SetShell();

        var usage = await editor.UsageAsync(code, cancellationToken);
        if (usage is null) return NotFound();

        return View(TierFormView, PlanFormPageViewModel.ForEdit(usage, PlanForm.From(usage.Plan)));
    }

    /// <summary>
    /// Rewrites a tier's code, name and four numbers, <b>and moves nobody</b>.
    ///
    /// <para>That is the architecture rather than an omission: assigning a plan copies its numbers
    /// onto the workspace's own row and nothing on any enforcement path joins back here, which is
    /// what makes a negotiated override and a per-customer quota history expressible at all. The
    /// form says it above the fields, and <see cref="Reapply"/> is the honest route for an operator
    /// who did mean "move everybody".</para>
    /// </summary>
    [HttpPost("/operator/plans/tiers/{code}")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> SaveTier(
        string code,
        [FromForm] PlanForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        SetShell();

        try
        {
            var saved = await editor.EditAsync(code, form.ToDraft(), cancellationToken);

            TempData[MessageKey] = UiText.PlanAdmin.TierSaved(saved.Name);

            return RedirectToAction(nameof(Tier), new { code = saved.Code });
        }
        catch (PlanEditRefusedException refusal)
        {
            var usage = await editor.UsageAsync(code, cancellationToken);
            var sentence = Sentence(refusal, code);

            return usage is null
                ? NotFound()
                : View(TierFormView, PlanFormPageViewModel.ForEdit(usage, form, sentence));
        }
    }

    /// <summary>
    /// Takes a tier off sale, or puts it back.
    ///
    /// <para><b>Retire, never delete.</b> Every workspace on a retired tier keeps working and keeps
    /// its numbers, because those numbers are on its own row — and the tier can still be re-applied
    /// to the workspaces already on it, which is how an edit reaches somebody the operator has
    /// stopped selling to.</para>
    /// </summary>
    [HttpPost("/operator/plans/tiers/{code}/retire")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Retire(
        string code,
        [FromForm] RetireTierForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        try
        {
            var tier = await editor.SetRetiredAsync(code, form.Retired, cancellationToken);

            TempData[MessageKey] = form.Retired
                ? UiText.PlanAdmin.TierRetired(tier.Name)
                : UiText.PlanAdmin.TierRestored(tier.Name);
        }
        catch (PlanEditRefusedException refusal)
        {
            TempData[ErrorKey] = Sentence(refusal, code);
        }

        return RedirectToAction(nameof(Operator));
    }

    /// <summary>Swaps a tier with its neighbour. The order is the operator's, not derived from any number.</summary>
    [HttpPost("/operator/plans/tiers/{code}/move")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Move(
        string code,
        [FromForm] MoveTierForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        try
        {
            var tier = await editor.MoveAsync(code, form.Direction, cancellationToken);

            TempData[MessageKey] = UiText.PlanAdmin.TierMoved(tier.Name);
        }
        catch (PlanEditRefusedException refusal)
        {
            TempData[ErrorKey] = Sentence(refusal, code);
        }

        return RedirectToAction(nameof(Operator));
    }

    /// <summary>
    /// Removes a tier nobody is on — the one created two minutes ago with a mis-typed code.
    ///
    /// <para>A tier a workspace is on is refused with a sentence naming retirement. The database
    /// would refuse it too, <c>Tenant.PlanId</c> being a <c>Restrict</c> foreign key, but it would
    /// arrive as a constraint violation on an operator's screen.</para>
    /// </summary>
    [HttpPost("/operator/plans/tiers/{code}/delete")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> DeleteTier(string code, CancellationToken cancellationToken)
    {
        try
        {
            var tier = await editor.UsageAsync(code, cancellationToken);
            if (tier is null) return NotFound();

            await editor.DeleteAsync(code, cancellationToken);

            TempData[MessageKey] = UiText.PlanAdmin.TierDeleted(tier.Plan.Name);
        }
        catch (PlanEditRefusedException refusal)
        {
            TempData[ErrorKey] = Sentence(refusal, code);
        }

        return RedirectToAction(nameof(Operator));
    }

    /// <summary>
    /// The confirmation in front of the one catalogue action that reaches customers: it shows how
    /// many workspaces are on the tier, how many of them actually hold different numbers, and that
    /// a negotiated ceiling on any of them is taken back.
    /// </summary>
    [HttpGet("/operator/plans/tiers/{code}/reapply")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Reapply(string code, CancellationToken cancellationToken)
    {
        SetShell();

        var usage = await editor.UsageAsync(code, cancellationToken);

        return usage is null ? NotFound() : View(ReapplyView, new ReapplyPlanPageViewModel(usage));
    }

    /// <summary>
    /// Copies the tier's numbers onto every workspace on it, in one transaction, through the one
    /// command that writes a <c>TenantQuotaChange</c> for each number that moves.
    ///
    /// <para>The reason is required for exactly that: a bulk move is the largest thing that can
    /// happen to a customer's ceiling, and the history row is what answers them afterwards.</para>
    /// </summary>
    [HttpPost("/operator/plans/tiers/{code}/reapply")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> ReapplyConfirmed(
        string code,
        [FromForm] ReapplyPlanForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(form.Reason))
        {
            TempData[ErrorKey] = UiText.Plans.ReasonRequired;
            return RedirectToAction(nameof(Reapply), new { code });
        }

        try
        {
            var moved = await editor.ReapplyAsync(code, form.Reason, CurrentUserId(), cancellationToken);

            TempData[MessageKey] = moved == 0
                ? UiText.PlanAdmin.ReapplyMovedNobody
                : UiText.PlanAdmin.ReapplyDone(moved);
        }
        catch (PlanEditRefusedException refusal)
        {
            TempData[ErrorKey] = Sentence(refusal, code);
        }

        return RedirectToAction(nameof(Tier), new { code });
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
    /// A refusal as the sentence an operator reads, in this request's language.
    ///
    /// <para><paramref name="planCode"/> is the tier the command named. The three refusals that
    /// quote <c>Plans:DefaultPlanCode</c> are only reachable when that setting names this very
    /// tier — that is the definition of each of them — so the code the sentence prints is the code
    /// the route already carried, and the controller needs no reading of configuration to say it.</para>
    /// </summary>
    private static string Sentence(PlanEditRefusedException refusal, string planCode) =>
        PlanRefusalText.For(refusal.Reason, planCode);

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
