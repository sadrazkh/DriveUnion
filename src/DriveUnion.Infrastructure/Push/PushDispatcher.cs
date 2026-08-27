using System.Text.Json;
using System.Text.Json.Serialization;
using DriveUnion.Core.Application;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// The whole of what crosses the wire, and deliberately not one byte more.
///
/// <para><b>There is no file name here, no workspace name, no slug and no identifier.</b> A push
/// payload is decrypted on the device and then belongs to the device: an operating system draws it
/// on a lock screen, files it in a notification centre, and may keep it there for days — through a
/// screenshot, a screen share, a shoulder. This product is sold on the server holding no readable
/// copy of a customer's files; a phone quietly accumulating «Q3-Report-Final.pdf finished» is that
/// claim with an exception in it. So the payload is a sentence with no nouns of the customer's in
/// it, and a path to the screen that does have them, behind their session.</para>
///
/// <para>The names are one letter each because a record is 4096 bytes and every byte spent on
/// <c>"title"</c> is a byte of Persian that cannot be spent. The service worker in
/// <c>wwwroot/sw-push.js</c> is the only reader and it is written down there.</para>
/// </summary>
/// <param name="Title">The bold line.</param>
/// <param name="Body">The sentence under it.</param>
/// <param name="Url">A path in this panel, never an absolute address.</param>
/// <param name="Tag">
/// The notification's identity on the device: a second one with the same tag replaces the first
/// rather than stacking beside it.
/// </param>
public sealed record PushPayload(
    [property: JsonPropertyName("t")] string Title,
    [property: JsonPropertyName("b")] string Body,
    [property: JsonPropertyName("u")] string Url,
    [property: JsonPropertyName("g")] string Tag);

/// <summary>
/// One event, to every device it is for, in each of those devices' own language.
///
/// <para><b>Composed per device rather than per event.</b> Two people in one workspace can be
/// reading the panel in two languages, and the words are already on a lock screen by the time
/// anybody could press a language switch. The culture is on the subscription row for exactly this —
/// there is no request here to read one from.</para>
///
/// <para><b>A device that turns out to be gone is removed as part of this.</b> The push service's
/// answer arrives here and nowhere else will ever get a better one; deferring it to a sweep would
/// mean the sweep has to guess. What this does not do is decide the rule — see
/// <c>PushSubscriptionStore.RecordAsync</c>, which is the one place a row dies.</para>
/// </summary>
public sealed class PushDispatcher(
    IPushSubscriptions subscriptions,
    IWebPushSender sender,
    IPushMessages messages,
    ILogger<PushDispatcher> logger) : IPushDispatcher
{
    public async Task<int> DeliverAsync(PushEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var devices = await subscriptions.ForAsync(notification.Audience, cancellationToken);

        if (devices.Count == 0) return 0;

        var reached = 0;

        foreach (var device in devices)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var text = messages.Compose(notification.Kind, notification.Count, device.Culture);

            var payload = JsonSerializer.Serialize(
                new PushPayload(text.Title, text.Body, text.Url, text.Tag),
                PayloadJson);

            var delivery = await sender.SendAsync(device, payload, cancellationToken);

            // Recorded before the next device is tried, and not batched at the end: a host stopping
            // halfway through a workspace's devices would otherwise lose every judgement it had
            // already collected, and the rows it lost are exactly the dead ones.
            await subscriptions.RecordAsync(device.Id, delivery, cancellationToken);

            if (delivery.Outcome == PushDeliveryOutcome.Accepted)
            {
                reached++;

                continue;
            }

            // The endpoint is deliberately absent from this line. It is a bearer credential for
            // posting to somebody's phone, and a log is the one place in this product that is read
            // by people who are not that person.
            logger.LogInformation(
                "Push to subscription {SubscriptionId} was {Outcome}: {Reason}",
                device.Id,
                delivery.Outcome,
                delivery.Reason);
        }

        return reached;
    }

    /// <summary>
    /// Compact, and non-ASCII written as itself.
    ///
    /// <para>The default encoder escapes every character outside Basic Latin to <c>\uXXXX</c>, which
    /// is six bytes for each letter of a Persian sentence — a record's worth of budget spent on an
    /// escaping nothing needs, since the body is AES-GCM'd and UTF-8 all the way to the worker.
    /// <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is
    /// «unsafe» for HTML contexts; this string is never in one — it is a JSON body inside an
    /// encrypted record, read by <c>JSON.parse</c>.</para>
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
