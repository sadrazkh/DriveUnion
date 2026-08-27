using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «پشتیبان فهرست» — whether the catalogue has been written into the pool lately, and the button
/// that writes one now.
///
/// <para><b>Operator-only, at an operator address.</b> Every route here is under <c>/operator/*</c>
/// behind the operator policy, the same shape the settings, plan and workspace screens use. A
/// snapshot spans every workspace at once and says which Google account holds each file, so a
/// customer must get a 403 rather than a screen — the whole product model rests on customers never
/// learning that a pool exists.</para>
///
/// <para><b>The page is the confirmation.</b> A backup nobody can see is a backup nobody trusts,
/// and one that has been failing since March is worse than none because somebody is relying on it.
/// So the screen leads with how old the newest good snapshot is and names the accounts holding
/// it — and it carries the restore steps, because the document they would otherwise be in lives
/// somewhere that might be gone too.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Operator)]
public sealed class BackupsController(ICatalogueSnapshots snapshots, TimeProvider clock) : Controller
{
    /// <summary>
    /// How many runs the table shows.
    ///
    /// <para>Twenty — a fortnight of nightly ones plus whatever was taken by hand around them.
    /// Enough to see a pattern of failures, short enough to read.</para>
    /// </summary>
    private const int PageSize = 20;

    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string MessageKey = "BackupMessage";

    private const string ErrorKey = "BackupError";

    [HttpGet("/operator/backups")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        var recent = await snapshots.RecentAsync(PageSize, cancellationToken);
        var newestGoodAt = await snapshots.NewestGoodAtAsync(cancellationToken);

        return View(new BackupsPageViewModel(
            [.. recent.Select(BackupRow.From)],
            newestGoodAt,
            clock.GetUtcNow(),
            TempData[MessageKey] as string,
            TempData[ErrorKey] as string));
    }

    /// <summary>
    /// Queues a snapshot for the worker rather than writing one here.
    ///
    /// <para>Writing it in the request would hold the connection open for as long as gzipping a
    /// hundred thousand rows and pushing them to Google takes — a form that times out on exactly the
    /// pool big enough to need this. The worker picks it up within the minute and the row is on the
    /// screen either way.</para>
    /// </summary>
    [HttpPost("/operator/backups/run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var result = await snapshots.RequestAsync(User.GetUserId(), cancellationToken);

        // Refused is not an error the operator has to do anything about — it means the thing they
        // asked for is already happening — but it is not «done» either, so it is said in the same
        // place and a different colour.
        if (result.Queued) TempData[MessageKey] = UiText.Backups.Queued;
        else TempData[ErrorKey] = UiText.Backups.AlreadyQueued;

        return RedirectToAction(nameof(Index));
    }

    // The pool's size and its daily quota are the operator's own figures and this page sets neither:
    // the shell asks the principal what to draw, which is the same claim this controller is
    // authorised on, so there is no per-page flag here for a page to set wrongly.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
