using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The product's fourth anonymous surface, after <c>/d/{slug}</c>, <c>/d/{slug}/file</c> and the OAuth
/// callback.
///
/// <para>Every request here arrives with no cookie, no principal and no tenant, and the identity in it
/// is a number anyone in the world can make Telegram send us. In production it is also bound to
/// loopback — our own Bot API server calls us back over <c>127.0.0.1</c>, and no nginx location routes
/// to it — so the set of clients that can reach it is "processes on this machine" rather than "the
/// internet". That is a large reduction and it changes exactly one control below. It does not make the
/// endpoint safe by itself: anything on the box that can open a socket can still POST an update, and
/// the Bot API server is not the only process there. <b>The secret token is the control.</b></para>
///
/// <para>Four things guard it, in the order they run:</para>
/// <list type="number">
/// <item><b>An unguessable path.</b> 32 random bytes, base64url, generated on registration and stored
/// encrypted. Obscurity is not the control; it keeps the route out of scanners' logs and off anything
/// that enumerates the panel's routes.</item>
/// <item><b>A trusted-source check.</b> Against our own server this is one entry that will never
/// change, and the forwarded-header problem disappears entirely because nginx is not in the path.
/// Empty in development, where the documented Telegram subnets are explicitly subject to change and an
/// allow-list built on them would be a control that fails closed on Telegram's schedule.</item>
/// <item><b>The secret token, compared in fixed time.</b> Missing or wrong is 401 and
/// <b>nothing is processed and nothing is logged beyond a counter</b> — a log line naming the value
/// that was tried is a log line naming values that were nearly right.</item>
/// <item><b>A body size limit.</b> Updates are small: a document update carries metadata, not bytes.
/// Without it this is an anonymous unbounded POST.</item>
/// </list>
///
/// <para><b>And it answers 200 immediately.</b> Telegram redelivers on a non-2xx or a timeout, so a
/// <c>sendDocument</c> inside this handler would hold the request open for minutes, guarantee the
/// redelivery, and have each redelivery start its own multi-gigabyte transfer. Short replies are sent
/// inline; anything that moves bytes is a queued row and the handler returns.</para>
/// </summary>
[Route("telegram")]
[AllowAnonymous]
public sealed class TelegramWebhookController(
    ITelegramBotSettingsStore botSettings,
    ITelegramUpdateParser parser,
    ITelegramUpdateHandler handler,
    IOptions<TelegramOptions> options,
    ILogger<TelegramWebhookController> logger) : ControllerBase
{
    /// <summary>
    /// The limiter policy this endpoint wants. It is referenced rather than applied because the
    /// policy table is registered elsewhere; see the deployment notes for the one line that adds it.
    /// </summary>
    public const string RateLimitPolicy = "DriveUnion.TelegramWebhook";

    /// <summary>
    /// 256 KiB. An update is metadata — a file's name, size and handle — and never its contents, so
    /// this is generous by two orders of magnitude and still a bound.
    /// </summary>
    public const int MaxBodyBytes = 256 * 1024;

    private readonly TelegramOptions _options = options.Value;

    /// <summary>
    /// <c>POST /telegram/{segment}</c>.
    ///
    /// <para>A literal route beats a parameter one in ASP.NET Core's precedence rules, so the
    /// operator's <c>bot</c> and the customer's <c>unlink</c> on this same prefix are unaffected by
    /// this catch-all sitting beside them.</para>
    /// </summary>
    [HttpPost("{segment}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Receive(string segment, CancellationToken cancellationToken)
    {
        if (!IsTrustedSource())
        {
            // Not 401: a caller outside the allow-list is not a caller with the wrong credential, and
            // answering the same way to both would tell an unauthorised source that the path exists.
            return NotFound();
        }

        var registration = await botSettings.ReadWebhookAsync(cancellationToken);

        if (registration is null) return NotFound();

        // The path is compared in fixed time as well. It is not the control, but it is a stored
        // secret, and a comparison that returns early on the first differing character is a
        // comparison that can be walked.
        if (!FixedTimeEquals(registration.PathSegment, segment)) return NotFound();

        var presented = Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();

        if (!FixedTimeEquals(registration.Secret, presented))
        {
            // A counter and nothing else. Not the header, not the path, not the address: a rejected
            // secret is somebody's guess, and a log of guesses is a log of near-misses.
            logger.LogWarning("A Telegram webhook POST was refused: the secret token did not match.");

            return Unauthorized();
        }

        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (parser.Parse(body) is not { } update) return Ok();

        try
        {
            var outcome = await handler.HandleAsync(update, cancellationToken);

            if (outcome is TelegramUpdateOutcome.Duplicate)
            {
                // A redelivery. The action was performed exactly once, by the first delivery, and the
                // correct answer is the one that stops Telegram sending it again.
                logger.LogDebug("A Telegram update was delivered more than once and was ignored.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Still 200. The update has already been claimed, so a non-2xx would buy a redelivery
            // that the ledger would then discard — the work would be lost either way, and a 500 would
            // add a retry storm to the loss.
            logger.LogError(ex, "A Telegram update could not be handled.");
        }

        return Ok();
    }

    /// <summary>
    /// Whether the caller is one of <c>Telegram:TrustedSubnets</c>. An empty list trusts everybody,
    /// which is what development against the cloud API has to accept — Telegram's own documented
    /// subnets are subject to change, and an allow-list built on them is a control that starts
    /// refusing real updates on somebody else's schedule.
    /// </summary>
    private bool IsTrustedSource()
    {
        if (_options.TrustedSubnets.Count == 0) return true;

        var address = HttpContext.Connection.RemoteIpAddress;
        if (address is null) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var entry in _options.TrustedSubnets)
        {
            if (Matches(address, entry)) return true;
        }

        return false;
    }

    private static bool Matches(IPAddress address, string entry)
    {
        var text = entry.Trim();
        if (text.Length == 0) return false;

        var slash = text.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0)
        {
            return IPAddress.TryParse(text, out var single) && single.Equals(address);
        }

        if (!IPAddress.TryParse(text[..slash], out var network)) return false;
        if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
        {
            return false;
        }

        if (network.AddressFamily != address.AddressFamily) return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();

        if (bits < 0 || bits > networkBytes.Length * 8) return false;

        var whole = bits / 8;
        var remainder = bits % 8;

        for (var i = 0; i < whole; i++)
        {
            if (networkBytes[i] != addressBytes[i]) return false;
        }

        if (remainder == 0) return true;

        var mask = (byte)(0xFF << (8 - remainder));

        return (networkBytes[whole] & mask) == (addressBytes[whole] & mask);
    }

    /// <summary>
    /// Fixed time, and length-safe. Comparing byte arrays of different lengths would leak the length
    /// through the early return that a naive implementation needs.
    /// </summary>
    private static bool FixedTimeEquals(string stored, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(stored)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }
}
