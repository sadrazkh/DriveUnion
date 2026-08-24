using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «سطل زباله» — what has been deleted, when the purge may take it, putting one back, and the button
/// that actually gives the space back.
///
/// <para><b>Emptying is the only thing here that frees anything, and the screen says so first.</b>
/// A customer who deletes a file, sees their usage figure stay where it was and is told nothing
/// concludes the product is broken — that report is what started this phase. The explanation is
/// therefore above the list rather than under it.</para>
///
/// <para>The tenant comes from the principal's claim and is passed explicitly into every call, which
/// is the whole of §8's isolation in one argument. A file id from another workspace is not refused
/// differently from one that has already been purged: the difference between two answers is a way of
/// asking whether somebody else's file exists.</para>
///
/// <para>Both writes are POSTs with an antiforgery token, and neither has a GET. A destructive
/// action on a GET is triggered by any <c>&lt;img src&gt;</c> anywhere and by a browser that
/// prefetches links — and «empty the trash» is the one action in this product that cannot be
/// undone.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[Route("trash")]
public sealed class TrashController(ITrash trash) : Controller
{
    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string NoticeKey = "TrashNotice";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var items = await trash.ListAsync(tenantId, cancellationToken);

        return View(new TrashPageViewModel(items, DateTimeOffset.UtcNow, TempData[NoticeKey] as string));
    }

    [HttpPost("{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var restored = await trash.RestoreAsync(tenantId, id, cancellationToken);

        TempData[NoticeKey] = restored ? UiText.Trash.Restored : UiText.Trash.NotRestored;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Destroys everything in this tenant's trash, whatever its deadline, and frees the bytes.
    ///
    /// <para>It reports how many files went rather than how many bytes came back. The count is what
    /// the customer can check against the list they were just looking at; a byte figure would be the
    /// one number on the screen nobody can verify, and the sidebar's capacity card answers "how much"
    /// on the very next render.</para>
    /// </summary>
    /// <remarks>
    /// Named <c>EmptyTrash</c> rather than <c>Empty</c> because <c>ControllerBase.Empty</c> already
    /// exists and is a result helper. The route segment is still <c>empty</c>, which is the word the
    /// address should carry.
    /// </remarks>
    [HttpPost("empty")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EmptyTrash(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var purged = await trash.EmptyAsync(tenantId, cancellationToken);

        TempData[NoticeKey] = purged == 0 ? UiText.Trash.EmptiedNothing : UiText.Trash.Emptied(purged);

        return RedirectToAction(nameof(Index));
    }

    // The pool's size and its daily quota are operator figures; a customer's sidebar shows neither.
    // The capacity card above the name is left for the shell to ask about, so it is the same card on
    // this screen as on every other one a customer opens.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
