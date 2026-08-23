using System.Net;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// Exponential backoff with full jitter for the two failures Google actually hands out under load:
/// 429, and 403 whose error reason is a rate limit. 5xx is in here too because a retried 503 costs
/// nothing and an unretried one costs somebody's upload.
///
/// Full jitter rather than a fixed schedule: the alternative is every stalled chunk in the process
/// waking up at the same millisecond and reproducing the burst that caused the throttle.
/// </summary>
public sealed class DriveRetryHandler : DelegatingHandler
{
    /// <summary>
    /// Set on a request whose body is a caller-owned, forward-only stream.
    ///
    /// CRITICAL. A resumable chunk PUT cannot be replayed: the stream has already been drained into
    /// the first attempt, so the retry sends an empty body under a <c>Content-Range</c> that claims
    /// 32 MiB. Google answers politely, acknowledges nothing, and its confirmed prefix stops
    /// advancing — which on the wire is indistinguishable from a stalled network, and stays that way
    /// until somebody reads a packet capture. So a request carrying this option is attempted exactly
    /// once and the rate limit is surfaced to the caller, who can re-probe the session for the
    /// confirmed length and send the chunk again from a stream that still has bytes in it.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NonRewindableBody =
        new("DriveUnion.NonRewindableBody");

    /// <summary>Four waits at most: roughly 0.5s, 1s, 2s, 4s before jitter.</summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Nothing waits longer than this, including a <c>Retry-After</c> Google asked for. A minute of
    /// silence inside a request the browser is still holding open is worse than an honest failure.
    /// </summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<DriveRetryHandler> _logger;
    private readonly TimeProvider _timeProvider;

    public DriveRetryHandler(ILogger<DriveRetryHandler> logger, TimeProvider timeProvider)
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

            if (attempt >= attempts)
            {
                return response;
            }

            var retryAfter = await GetRetryDelayAsync(response, attempt, cancellationToken)
                .ConfigureAwait(false);
            if (retryAfter is null)
            {
                return response;
            }

            _logger.LogWarning(
                "Google answered {StatusCode} for {Method} {Uri}; retrying in {Delay} "
                + "(attempt {Attempt} of {Attempts}).",
                (int)response.StatusCode,
                request.Method,
                request.RequestUri,
                retryAfter.Value,
                attempt,
                attempts);

            // Free the connection before sleeping on it.
            response.Dispose();

            await Task.Delay(retryAfter.Value, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long to wait before trying again, or null when this response is not retryable.</summary>
    private async Task<TimeSpan?> GetRetryDelayAsync(
        HttpResponseMessage response,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (!await IsRetryableAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // An explicit Retry-After beats our own arithmetic: it is the only number in the exchange
        // that reflects what the server actually knows.
        var stated = ReadRetryAfter(response);
        if (stated is not null)
        {
            return stated.Value > MaxDelay ? MaxDelay : stated.Value;
        }

        var ceiling = BaseDelay * Math.Pow(2, attempt - 1);
        if (ceiling > MaxDelay)
        {
            ceiling = MaxDelay;
        }

        // Full jitter: uniform in [0, ceiling], not ceiling ± a wobble.
        return ceiling * Random.Shared.NextDouble();
    }

    private static async Task<bool> IsRetryableAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if ((int)response.StatusCode >= 500 && response.StatusCode != HttpStatusCode.NotImplemented)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // A 403 is usually final — wrong scope, no permission, account out of space. Only the
        // rate-limit reasons are worth another attempt, so the body has to be read to tell them
        // apart. ReadAsStringAsync buffers, so the caller can still read it afterwards.
        if (response.Headers.RetryAfter is not null)
        {
            // Google told us when to come back. Whatever the reason string says, that is an
            // instruction to retry.
            return true;
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return GoogleApiError.Parse(body).IsRateLimit;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // An unreadable error body is not a reason to hammer Google.
            return false;
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            // A date in the past means "now"; the clock skew between us and Google is not our
            // problem to solve here.
            var wait = date - _timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
