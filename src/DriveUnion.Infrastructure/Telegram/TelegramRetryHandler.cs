using System.Net;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// Exponential backoff with full jitter for what Telegram actually hands out under load: 429 with a
/// <c>retry_after</c>, and 5xx from a Bot API server that is busy or restarting.
///
/// <para>Full jitter rather than a fixed schedule, for the reason the Drive handler gives: the
/// alternative is every stalled call in the process waking at the same millisecond and reproducing
/// the burst that caused the throttle.</para>
///
/// <para><b>This handler is not the flood-control policy.</b> A 429 that reaches the outbox is parked
/// until the instant Telegram named and does not spend an attempt — that decision belongs to the
/// drainer, which is the only place that knows an item is a queued unit of work rather than a call.
/// What this handler covers is the short retry that keeps a transient 502 from becoming a visible
/// failure, and it deliberately gives up quickly so the outbox's own policy is what governs.</para>
/// </summary>
public sealed class TelegramRetryHandler : DelegatingHandler
{
    /// <summary>
    /// Set on a request whose body is a caller-owned, forward-only stream — which is every document
    /// upload.
    ///
    /// CRITICAL, and the same trap as a resumable chunk PUT: the stream has already been drained into
    /// the first attempt, so a retry sends an empty or partial body under a content length that
    /// claims two gigabytes. The server does not say so; the call simply fails in a way that looks
    /// like a network fault. A request carrying this option is attempted exactly once and the failure
    /// is surfaced to the caller, who still has the file and can start again from the beginning.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NonRewindableBody =
        new("DriveUnion.Telegram.NonRewindableBody");

    private const int MaxAttempts = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Nothing waits longer than this here, including a <c>retry_after</c> Telegram asked for. A long
    /// flood-control park is the outbox's job — it can put the item down and pick up another tenant's
    /// work — and holding a worker asleep instead is how one chat's throttle becomes everybody's.
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<TelegramRetryHandler> _logger;
    private readonly TimeProvider _timeProvider;

    public TelegramRetryHandler(ILogger<TelegramRetryHandler> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var replayable = !request.Options.TryGetValue(NonRewindableBody, out var nonRewindable)
                         || !nonRewindable;
        var attempts = replayable ? MaxAttempts : 1;

        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (attempt >= attempts || !IsRetryable(response)) return response;

            var delay = Delay(response, attempt);

            // Never the request URI. The bot token travels in the path — this is the one place in
            // the product where a credential is not in a header — so a log line naming the URL is the
            // token in a log file.
            _logger.LogWarning(
                "Telegram answered {StatusCode}; retrying in {Delay} (attempt {Attempt} of {Attempts}).",
                (int)response.StatusCode,
                delay,
                attempt,
                attempts);

            response.Dispose();

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (int)response.StatusCode >= 500;

    private TimeSpan Delay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } stated)
        {
            var wait = stated < TimeSpan.Zero ? TimeSpan.Zero : stated;
            return wait > MaxDelay ? MaxDelay : wait;
        }

        var ceiling = BaseDelay * Math.Pow(2, attempt - 1);
        if (ceiling > MaxDelay) ceiling = MaxDelay;

        return ceiling * Random.Shared.NextDouble();
    }
}
