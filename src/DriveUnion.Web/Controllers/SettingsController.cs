using System.Security.Claims;
using DriveUnion.Core.Settings;
using DriveUnion.Infrastructure.Settings;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «تنظیمات پنل» — the settings that belong to no workspace, which is one so far: how long the trash
/// keeps a file.
///
/// <para><b>Operator-only, at an operator address.</b> Every route here is under <c>/operator/*</c>
/// behind the operator policy, the same shape the plan and workspace screens use. Retention is a
/// decision about the operator's own storage — how long their pool carries bytes a customer has
/// already deleted — so a customer must get a 403 rather than a screen with a number on it they
/// cannot change.</para>
///
/// <para>Out of range is refused rather than clamped, and nothing is written when it is. The store
/// clamps on the way in so a hand-edited row cannot make the sweeper and the screen disagree, but a
/// form that silently turned a typed 9999 into 365 would tell an operator they had set something
/// they had not.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Operator)]
public sealed class SettingsController(IOperatorSettingsStore settings) : Controller
{
    /// <summary>Carries one sentence across the redirect that follows a write. Strings only.</summary>
    private const string MessageKey = "SettingsMessage";

    private const string ErrorKey = "SettingsError";

    [HttpGet("/operator/settings")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        var stored = await settings.ReadAsync(cancellationToken);

        return View(new OperatorSettingsPageViewModel(
            stored,
            TempData[MessageKey] as string,
            TempData[ErrorKey] as string));
    }

    /// <summary>
    /// Stores the window that will be stamped on the next deletion.
    ///
    /// <para>It reaches nothing already in a trash. The window is read when a file is deleted and
    /// written onto that file's own deadline, so what somebody deleted yesterday keeps the promise
    /// it was given — and the screen says so above the field rather than in a release note.</para>
    /// </summary>
    [HttpPost("/operator/settings/retention")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retention(
        [FromForm] RetentionForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.Days < OperatorSettings.MinimumTrashRetentionDays
            || form.Days > OperatorSettings.MaximumTrashRetentionDays)
        {
            TempData[ErrorKey] = UiText.OperatorSettings.RefusedOutOfRange(
                OperatorSettings.MinimumTrashRetentionDays,
                OperatorSettings.MaximumTrashRetentionDays);

            return RedirectToAction(nameof(Index));
        }

        var stored = await settings.SaveTrashRetentionAsync(form.Days, CurrentUserId(), cancellationToken);

        TempData[MessageKey] = UiText.OperatorSettings.Saved(stored.TrashRetentionDays);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The operator who pressed the button, or null when the principal carries no usable id.
    ///
    /// <para>Null rather than <c>Guid.Empty</c>, the same rule the plan screens follow: an empty id
    /// in an audit column is a person who does not exist, and this is the one setting in the product
    /// that decides when somebody else's bytes are destroyed.</para>
    /// </summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : null;

    // The pool's size and its daily quota are the operator's own figures, and this page sets
    // neither: the shell asks the principal what to draw, which is the same claim this controller is
    // authorised on, so there is no per-page flag here for a page to set wrongly.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
