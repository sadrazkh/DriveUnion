using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Application;
using DriveUnion.Core.Notifications;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// One encrypted body, posted to one push service, and the one judgement that matters: is this
/// endpoint still there.
///
/// <para><b>Nothing here decides what to do about the answer.</b> A 410 becomes
/// <see cref="PushDeliveryOutcome.Gone"/> and a timeout becomes
/// <see cref="PushDeliveryOutcome.Failed"/>, and <c>PushSubscriptionStore.RecordAsync</c> is the one
/// place that turns those into a deleted row. Two places deciding when a subscription dies is one
/// place keeping dead rows.</para>
///
/// <para><b>The body is opaque to the push service and to this class.</b> It arrives already
/// encrypted — see <c>WebPushEncryption</c> — and everything on the request that is not the body is
/// routing: a TTL, a topic, and the VAPID token that says who is sending. There is nothing readable
/// on this request at all, which is the property the whole protocol exists for.</para>
/// </summary>
public sealed class WebPushSender(
    IHttpClientFactory http,
    IVapidCredentials vapid,
    TimeProvider clock) : IWebPushSender
{
    /// <summary>The named client, so its timeout is configured in one place and not per call.</summary>
    public const string ClientName = "web-push";

    /// <summary>
    /// The content coding, which is also the only one this implements.
    ///
    /// <para><c>aesgcm</c> — the draft coding that preceded RFC 8188 — is deliberately absent. Every
    /// browser that can install this panel as a home-screen app supports <c>aes128gcm</c>, and
    /// carrying the older one would mean carrying a second key derivation whose failure mode is a
    /// notification that silently never arrives.</para>
    /// </summary>
    public const string ContentEncoding = "aes128gcm";

    /// <summary>
    /// How long the push service may hold a message for a device that is offline.
    ///
    /// <para>Four hours. A notification is «this finished» and the panel itself is the record: a
    /// phone that has been off for a day is better served by opening the app than by six stale
    /// notices arriving at once. Zero would mean «deliver now or discard», which throws the message
    /// away for a phone that is merely asleep — which is every phone, most of the time, and the
    /// exact case this feature is for.</para>
    /// </summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(4);

    public async Task<PushDelivery> SendAsync(
        PushSubscription subscription,
        string payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint))
        {
            // Not «failed»: an endpoint that is not a URL will not become one, and counting it five
            // times before removing it is five sends nobody could have made.
            return PushDelivery.Gone("The endpoint is not an absolute URL.");
        }

        if (Base64UrlText.Decode(subscription.P256dh) is not { } receiverKey
            || Base64UrlText.Decode(subscription.Auth) is not { } authSecret)
        {
            return PushDelivery.Gone("The subscription's keys are not base64.");
        }

        using var signingKey = vapid.CreateSigningKey();

        if (signingKey is null)
        {
            // Configuration, not this device. Reported as a failure so the counter moves and a log
            // line appears, and never as «gone»: a deployment that has lost its keys for an
            // afternoon must not come back to an empty table.
            return PushDelivery.Failed($"{VapidCredentials.SectionName} keys are not configured.");
        }

        byte[] body;

        try
        {
            body = WebPushEncryption.Encrypt(receiverKey, authSecret, WebPushEncryption.Utf8(payload));
        }
        catch (Exception exception) when (exception is ArgumentException
            or System.Security.Cryptography.CryptographicException
            or NotSupportedException)
        {
            // A payload past one record, or a p256dh that decoded to 65 bytes and is not a point on
            // P-256 — which is refused by the key import rather than by any check of ours, because
            // the import is the only thing that can tell. All three are this row or this message
            // being wrong rather than the push service being down, so the row goes.
            //
            // Caught here and not left to the worker's catch-all, which is the whole point: an
            // exception escaping this method abandons the rest of the workspace's devices, so one
            // corrupt row would silence everybody else's notifications and the log line would name
            // the event rather than the row. Three types, because which one a bad point produces is
            // the platform's business — OpenSSL raises CryptographicException and Windows CNG wraps
            // the same thing in PlatformNotSupportedException.
            return PushDelivery.Gone(exception.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add(ContentEncoding);

        // RFC 8030 §5.2. Required by every push service in use; a request without it is a 400 from
        // Mozilla's and an accepted-then-discarded message from others.
        request.Headers.TryAddWithoutValidation(
            "TTL",
            ((int)TimeToLive.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        // «This is not the most important thing that will happen today.» It is what lets a phone
        // batch deliveries rather than waking its radio for each one, and it is the honest value for
        // everything this product sends.
        request.Headers.TryAddWithoutValidation("Urgency", "normal");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            VapidTokens.Scheme,
            AuthorizationParameters(signingKey, endpoint));

        try
        {
            using var client = http.CreateClient(ClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return Judge(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping. Not this endpoint's fault, and it must not count against it.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // A timeout, a refused connection, DNS. The counter is what tells a bad afternoon from a
            // push service this deployment can no longer reach at all.
            return PushDelivery.Failed(exception.Message);
        }
    }

    /// <summary>
    /// The <c>t=</c> and <c>k=</c> half of the header, built here so the scheme is set by
    /// <see cref="AuthenticationHeaderValue"/> rather than concatenated into it.
    /// </summary>
    private string AuthorizationParameters(System.Security.Cryptography.ECDsa key, Uri endpoint)
    {
        var whole = VapidTokens.Authorization(
            key,
            endpoint,
            vapid.Subject,
            clock.GetUtcNow() + VapidTokens.Lifetime);

        return whole[(VapidTokens.Scheme.Length + 1)..];
    }

    /// <summary>
    /// What a status code means for this row.
    ///
    /// <para>404 and 410 and nothing else are «gone». A 403 is deliberately not: it is what a wrong
    /// VAPID key pair produces, and a wrong key pair is wrong for every device at once — treating it
    /// per row would empty the whole table over a configuration mistake somebody could fix in a
    /// minute. A 429 is not either; it is the push service asking for less, which is a bad minute by
    /// definition.</para>
    /// </summary>
    private static PushDelivery Judge(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.Gone =>
            PushDelivery.Gone($"The push service answered {(int)status}."),

        // 201 is what RFC 8030 specifies and what every service in use returns; 200 and 202 are
        // accepted because two of them have returned each at some point and a message that was taken
        // is a message that was taken.
        HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NoContent =>
            PushDelivery.Accepted,

        _ => PushDelivery.Failed($"The push service answered {(int)status}."),
    };
}
