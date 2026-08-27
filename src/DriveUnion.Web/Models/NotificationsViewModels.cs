using DriveUnion.Infrastructure.Push;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// The notifications screen: what this deployment can offer, and what this person already has.
///
/// <para><b>What is deliberately not on it: anything about a device.</b> No name, no browser, no
/// address, no «last seen from». A count is enough for «is this on», and every one of the others is
/// a record of where somebody was, kept on a server, to no end — the row does not carry them for
/// that reason and this cannot show what does not exist.</para>
///
/// <para>Whether the control can actually be used is not decided here and cannot be: the answer
/// depends on whether this browser is a home-screen app and what it has already been asked, which is
/// only knowable in the page. The server decides whether the control is <i>worth</i> offering — see
/// <see cref="ApplicationServerKey"/> — and <c>Scripts/notifications.ts</c> decides which of the
/// four states the reader is actually in.</para>
/// </summary>
/// <param name="ApplicationServerKey">
/// The VAPID public key, base64url, or null when this deployment has none.
///
/// <para>Null is the whole gate. A browser will happily mint a subscription against any 65 bytes,
/// and every send to it would then be a 403 for the life of the row — so a deployment with no keys
/// must not draw a button, rather than drawing one that appears to work.</para>
/// </param>
/// <param name="Problem">
/// Why the keys are unusable, for an operator and never for a customer. It names configuration keys,
/// which is the operator's own vocabulary and nobody else's business.
/// </param>
public sealed record NotificationsPageViewModel(
    string? ApplicationServerKey,
    VapidState VapidState,
    string? Problem,
    int DeviceCount,
    string SubscribeUrl,
    string UnsubscribeUrl,
    string AntiforgeryHeader,
    string AntiforgeryToken)
{
    public bool CanSubscribe => ApplicationServerKey is { Length: > 0 };

    /// <summary>What the card says when the operator has not set the keys up.</summary>
    public string DeviceCountText => DeviceCount == 0
        ? UiText.Notifications.NoDevices
        : UiText.Notifications.DeviceCount(Numerals.Count(DeviceCount));
}

/// <summary>
/// What a browser hands over when it subscribes: the mailbox and the two keys a message is
/// encrypted to.
/// </summary>
/// <param name="Endpoint">The push service's URL for this device.</param>
/// <param name="P256dh">The device's public key, base64url.</param>
/// <param name="Auth">The device's authentication secret, base64url.</param>
public sealed record PushSubscriptionPayload(string? Endpoint, string? P256dh, string? Auth);

/// <summary>Just the endpoint, for forgetting one device.</summary>
public sealed record PushUnsubscribePayload(string? Endpoint);
