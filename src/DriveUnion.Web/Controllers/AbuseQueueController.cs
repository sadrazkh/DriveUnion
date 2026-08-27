using DriveUnion.Core.Application;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// What the operator does about a complaint.
///
/// <para><b>Why this screen exists at all.</b> The storage under every workspace is a pool of Google
/// accounts the operator owns. One file reported to Google gets the account holding it suspended,
/// and that account holds the files of every workspace that happened to land on it — so a single
/// complaint nobody read is how dozens of paying customers lose everything at once. Reaching the
/// file before Google does is the only defence, and a queue is the only way to reach it.</para>
///
/// <para><b>There is no viewer here and there is not going to be one.</b> Judging a report means
/// opening the public link, which is exactly what the reporter did. A panel that let an operator
/// open any customer's file on the strength of a stranger's accusation would be a much larger thing
/// than the problem it solves, and it would be a promise this product has already made in the other
/// direction — see <c>AbuseReportView.Slug</c>.</para>
///
/// <para>Operator authority is the route surface, as everywhere else under <c>/operator/*</c>: a
/// customer who types this address is refused by the authorisation middleware, not shown a page with
/// the buttons missing.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Operator)]
[AutoValidateAntiforgeryToken]
public sealed class AbuseQueueController(IAbuseQueue queue, ILogger<AbuseQueueController> logger)
    : Controller
{
    private const string MessageKey = "AbuseMessage";

    /// <param name="all">
    /// Off by default. The queue is what is waiting; the history is a second click, because a list
    /// that opens on everything ever decided is a list with the work buried in it.
    /// </param>
    [HttpGet("/operator/abuse")]
    public async Task<IActionResult> Index(bool all, CancellationToken cancellationToken)
    {
        var reports = await queue.ListAsync(openOnly: !all, cancellationToken);

        // Asked for separately rather than counted off the rows above: with `all` on, those rows are
        // every report there has ever been, and the number beside the toggle has to keep meaning
        // «waiting» in both views.
        var open = all
            ? await queue.OpenCountAsync(cancellationToken)
            : reports.Count;

        return View(new AbuseQueuePageViewModel(
            [.. reports.Select(AbuseRowViewModel.From)],
            all,
            open,
            TempData[MessageKey] as string));
    }

    /// <summary>Takes the link down. See <see cref="IAbuseQueue.UpholdAsync"/> for why not the file.</summary>
    [HttpPost("/operator/abuse/{id:guid}/uphold")]
    public async Task<IActionResult> Uphold(
        Guid id,
        string? resolution,
        bool all,
        CancellationToken cancellationToken)
    {
        var acted = await queue.UpholdAsync(id, Operator(), resolution, cancellationToken);

        if (acted)
        {
            // Logged because this is an operator reaching into a customer's workspace. Nothing about
            // the file or the complaint goes in the line — a report's contents are somebody's
            // accusation about somebody else, and a log is the wrong place to keep either.
            logger.LogInformation("An operator upheld abuse report {ReportId} and revoked its link.", id);
        }

        return Back(acted, all);
    }

    [HttpPost("/operator/abuse/{id:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid id,
        string? resolution,
        bool all,
        CancellationToken cancellationToken)
    {
        var acted = await queue.RejectAsync(id, Operator(), resolution, cancellationToken);

        if (acted) logger.LogInformation("An operator rejected abuse report {ReportId}.", id);

        return Back(acted, all);
    }

    /// <summary>
    /// Stops every public link a workspace has.
    ///
    /// <para>Separate from upholding because it is a different decision about a different subject:
    /// one is «this file should not be reachable», the other is «this customer should not be
    /// publishing». It resolves no report — the queue still holds each complaint, and each still
    /// has to be judged, because lifting the suspension puts every one of those links back.</para>
    /// </summary>
    [HttpPost("/operator/abuse/tenant/{tenantId:guid}/suspend")]
    public async Task<IActionResult> Suspend(
        Guid tenantId,
        string? reason,
        bool all,
        CancellationToken cancellationToken)
    {
        var acted = await queue.SuspendTenantAsync(tenantId, Operator(), reason, cancellationToken);

        if (acted)
        {
            logger.LogWarning(
                "An operator suspended every public link belonging to workspace {TenantId}.",
                tenantId);
        }

        return Back(acted, all);
    }

    [HttpPost("/operator/abuse/tenant/{tenantId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid tenantId, bool all, CancellationToken cancellationToken)
    {
        var acted = await queue.RestoreTenantAsync(tenantId, cancellationToken);

        if (acted)
        {
            logger.LogInformation(
                "An operator restored the public links of workspace {TenantId}.",
                tenantId);
        }

        return Back(acted, all);
    }

    /// <summary>
    /// Back to the list the operator was reading, with one sentence about what happened.
    ///
    /// <para><paramref name="all"/> is carried through every POST so that acting on a resolved row
    /// from the history does not silently drop the reader back into the waiting queue.</para>
    /// </summary>
    private IActionResult Back(bool acted, bool all)
    {
        // The failure is a race, not a mistake: two operators on the same row, or a back button. It
        // says what is true now rather than apologising, because there is nothing to fix.
        TempData[MessageKey] = acted ? UiText.Abuse.Done : UiText.Abuse.NothingToDo;

        return RedirectToAction(nameof(Index), all ? new { all = true } : null);
    }

    /// <summary>
    /// Who is acting, from the principal and never from the form.
    ///
    /// <para>A user id a caller can name is a user id a caller can name somebody else's, and this
    /// one is written on the row as «who decided this». It may be null — see <see cref="IAbuseQueue"/>
    /// for why that is the right shape rather than a throw.</para>
    /// </summary>
    private Guid? Operator() => User.GetUserId();
}
