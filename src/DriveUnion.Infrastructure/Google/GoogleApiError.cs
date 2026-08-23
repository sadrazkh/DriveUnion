using System.Text.Json;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// What could be salvaged from a Google error body.
///
/// Every field is optional because the body is not a contract. Drive answers with
/// <c>{"error":{"code":403,"message":"…","errors":[{"reason":"userRateLimitExceeded",…}]}}</c> most
/// of the time, occasionally with a bare <c>{"error":"invalid_grant"}</c> from the OAuth endpoint,
/// and with an HTML page when something in front of the API is having a bad day. Nothing downstream
/// may depend on any of it being present.
/// </summary>
public sealed record GoogleApiError(string? Message, IReadOnlyList<string> Reasons)
{
    public static readonly GoogleApiError None = new(null, []);

    /// <summary>
    /// Reasons that mean "you are going too fast, try again", as distinct from reasons that mean
    /// "this will not work no matter how often you ask".
    ///
    /// UNCONFIRMED: these strings come from Google's published error tables and have not been seen
    /// on the wire from this codebase — there are no credentials on this machine. <c>quotaExceeded</c>
    /// and <c>dailyLimitExceeded</c> are deliberately absent: the first can mean the account is out
    /// of storage, and the second does not clear within any backoff worth waiting for.
    /// </summary>
    private static readonly HashSet<string> RateLimitReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "rateLimitExceeded",
        "userRateLimitExceeded",
        "sharingRateLimitExceeded",
        "RESOURCE_EXHAUSTED",
    };

    public bool IsRateLimit => Reasons.Any(RateLimitReasons.Contains);

    /// <summary>
    /// Best effort. A body that will not parse produces <see cref="None"/> rather than an exception,
    /// because the caller is already handling a failure and a second one helps nobody.
    /// </summary>
    public static GoogleApiError Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return None;
            }

            // The OAuth endpoint puts a bare reason string here; the Drive API puts an object.
            if (error.ValueKind == JsonValueKind.String)
            {
                var reason = error.GetString();
                var description = document.RootElement.TryGetProperty("error_description", out var d)
                    ? d.GetString()
                    : null;
                return new GoogleApiError(description ?? reason, reason is null ? [] : [reason]);
            }

            if (error.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;

            var reasons = new List<string>();
            if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                // The newer google.rpc shape carries the machine-readable part here instead.
                var value = status.GetString();
                if (value is not null)
                {
                    reasons.Add(value);
                }
            }

            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String)
                    {
                        var value = reason.GetString();
                        if (value is not null)
                        {
                            reasons.Add(value);
                        }
                    }
                }
            }

            return new GoogleApiError(message, reasons);
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>A one-line description safe to put in an exception message. Never contains a token.</summary>
    public string Describe()
    {
        if (Message is null && Reasons.Count == 0)
        {
            return "no error detail";
        }

        return Reasons.Count == 0
            ? Message!
            : $"{Message ?? "no message"} [{string.Join(", ", Reasons)}]";
    }
}
