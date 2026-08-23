namespace DriveUnion.Core.Storage;

public enum GoogleAccountStatus
{
    /// <summary>Connected, token refreshable, accepting uploads.</summary>
    Healthy = 0,

    /// <summary>Refresh failed. The operator has to reconnect; uploads must not be routed here.</summary>
    Disconnected = 1,

    /// <summary>Connected but withheld from upload routing by the operator.</summary>
    Paused = 2,
}

/// <summary>
/// One Google Drive account in the operator's pool.
///
/// Deliberately has no tenant. Customers never authenticate with Google and must never learn which
/// account holds their file — that is what keeps OAuth verification and the CASA assessment off the
/// critical path, and it only stays true if nothing here leaks into a tenant-facing response.
/// </summary>
public sealed class GoogleAccount
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    /// <summary>Short operator-facing handle: <c>A1</c>, <c>A2</c>. Shown on the account cards.</summary>
    public required string Label { get; set; }

    /// <summary>Encrypted at rest. Never logged, never returned by an API.</summary>
    public required string RefreshTokenProtected { get; set; }

    /// <summary>Encrypted at rest. Google access tokens live about an hour.</summary>
    public string? AccessTokenProtected { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    /// <summary>Root folder id of <c>DriveUnion/</c> inside this account, created on first upload.</summary>
    public string? RootFolderId { get; set; }

    public long QuotaTotalBytes { get; set; }

    public long QuotaUsedBytes { get; set; }

    public GoogleAccountStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// True when the cached access token is missing or close enough to expiry that a request started
    /// now could outlive it. The 60-second skew is not politeness — a chunk upload begun at T+3599
    /// fails halfway through with a 401 that looks like a network fault.
    /// </summary>
    public bool NeedsAccessToken(DateTimeOffset now) =>
        AccessTokenProtected is null
        || AccessTokenExpiresAt is null
        || now >= AccessTokenExpiresAt.Value.AddSeconds(-60);
}
