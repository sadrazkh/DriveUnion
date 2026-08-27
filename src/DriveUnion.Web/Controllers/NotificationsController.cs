using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Push;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «اعلان‌ها» — one screen for both audiences, and the two calls the page makes.
///
/// <para><b>Signed in, and nothing more.</b> Not the tenant policy and not the operator policy: a
/// customer subscribes to hear about their own fetches and deletions, an operator subscribes to hear
/// about abuse reports, and an operator has no workspace at all — so a tenant policy here would lock
/// the operator out of the one notification that is racing Google. What each of them is actually
/// told is decided by the audience of the event, not by this route.</para>
///
/// <para><b>Why the two writes are under <c>/api/</c>.</b> They are called from a fetch, not from a
/// form, because a subscription can only be obtained inside the promise a user gesture started — a
/// form post would navigate away from the page holding it. <c>/api/</c> is also the prefix the
/// service worker refuses to touch at all (see <c>wwwroot/sw.js</c>), which is what keeps a
/// registration request off any cache. They are the panel's own JSON and are behind the panel's
/// cookie, exactly like <c>/api/uploads</c> and <c>/api/files</c> — the bearer-key API lives at
/// <c>/api/v1</c> and this is not part of it.</para>
/// </summary>
[Authorize]
public sealed class NotificationsController(
    IVapidCredentials vapid,
    IPushSubscriptions subscriptions,
    IAntiforgery antiforgery) : Controller
{
    [HttpGet("/notifications")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        var status = vapid.Describe();

        var devices = User.GetUserId() is { } userId
            ? await subscriptions.CountForUserAsync(userId, cancellationToken)
            : 0;

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);

        return View(new NotificationsPageViewModel(
            status.PublicKey,
            status.State,

            // The configuration detail is the operator's and only the operator's. A customer is told
            // that the operator has not set this up, which is true, actionable for them (ask) and
            // says nothing about how the deployment is configured.
            User.IsOperator() ? status.Problem : null,
            devices,
            Url.Action(nameof(Subscribe)) ?? "/api/notifications/subscribe",
            Url.Action(nameof(Unsubscribe)) ?? "/api/notifications/unsubscribe",
            tokens.HeaderName ?? "RequestVerificationToken",
            tokens.RequestToken ?? string.Empty));
    }

    /// <summary>
    /// Records this device.
    ///
    /// <para>The tenant and the user come off the principal and never off the payload — the same
    /// rule <c>UploadsController.Begin</c> keeps. A caller who could name the workspace their
    /// device belongs to could subscribe to somebody else's notifications.</para>
    /// </summary>
    [HttpPost("/api/notifications/subscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscribe(
        [FromBody] PushSubscriptionPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (User.GetUserId() is not { } userId) return Forbid();

        if (payload.Endpoint is not { Length: > 0 } endpoint
            || payload.P256dh is not { Length: > 0 } p256dh
            || payload.Auth is not { Length: > 0 } auth)
        {
            return BadRequest();
        }

        // Refused rather than stored, when this deployment could never send to it. A subscription
        // minted against a key the server does not hold the private half of is a row that answers
        // 403 for ever, and the reader would have been asked for a permission that buys them
        // nothing.
        if (!vapid.Describe().IsReady) return Conflict();

        var saved = await subscriptions.SaveAsync(
            User.GetTenantId(),
            userId,
            endpoint,
            p256dh,
            auth,

            // The device's language, taken from the resolved culture of this request rather than
            // from the payload: it is the language the reader is looking at right now, and it is the
            // only thing that will decide what their lock screen says months from now.
            PanelCulture.Code,
            cancellationToken);

        return saved.Refusal switch
        {
            PushSubscriptionRefusal.None => Ok(),
            PushSubscriptionRefusal.TooMany => StatusCode(StatusCodes.Status429TooManyRequests),
            _ => BadRequest(),
        };
    }

    /// <summary>
    /// Forgets this device.
    ///
    /// <para>Answers 204 whether or not there was a row. The page calls this after the browser has
    /// already given up its subscription, so «there was nothing to remove» is a success from the
    /// reader's point of view — and a 404 here would be this endpoint confirming whether an endpoint
    /// somebody named is registered.</para>
    /// </summary>
    [HttpPost("/api/notifications/unsubscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] PushUnsubscribePayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (User.GetUserId() is not { } userId) return Forbid();
        if (payload.Endpoint is not { Length: > 0 } endpoint) return BadRequest();

        await subscriptions.RemoveAsync(userId, endpoint, cancellationToken);

        return NoContent();
    }

    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
