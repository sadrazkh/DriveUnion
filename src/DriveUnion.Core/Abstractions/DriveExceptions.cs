namespace DriveUnion.Core.Abstractions;

public class DriveApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Google answered 403 <c>userRateLimitExceeded</c> or 429. The transport retries with exponential
/// backoff and jitter; this surfaces only when the retries are exhausted.
/// </summary>
public sealed class DriveRateLimitedException(string message, TimeSpan? retryAfter = null)
    : DriveApiException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The resumable session URI is dead — Drive keeps them for about a week. The upload cannot be
/// resumed and the client has to start over, which is worth saying plainly rather than letting each
/// chunk fail on its own.
/// </summary>
public sealed class DriveUploadSessionExpiredException(string message) : DriveApiException(message);

/// <summary>The account's credentials could not be refreshed. The operator has to reconnect it.</summary>
public sealed class DriveAccountUnavailableException(string message) : DriveApiException(message);
