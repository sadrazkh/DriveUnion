using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

public sealed record GoogleAccountSummary(
    Guid Id,
    string Email,
    string Label,
    GoogleAccountStatus Status,
    long QuotaTotalBytes,
    long QuotaUsedBytes,
    DateTimeOffset CreatedAt);

/// <summary>
/// The operator's pool. There is no tenant parameter anywhere here and there must never be one —
/// these accounts belong to the operator, and every endpoint built on this interface is
/// operator-only.
/// </summary>
public interface IGoogleAccountDirectory
{
    Task<IReadOnlyList<GoogleAccountSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Exchanges an OAuth authorization code for tokens and stores them encrypted. Returns the new
    /// account's id.
    /// </summary>
    Task<Guid> ConnectAsync(
        string authorizationCode,
        string redirectUri,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an account disconnected. It does not revoke the token: a revoked token breaks every
    /// live /d/{slug} backed by that account with no way back until M3 can evacuate the files.
    /// </summary>
    Task<bool> DisconnectAsync(Guid accountId, CancellationToken cancellationToken);

    Task RefreshQuotaAsync(Guid accountId, CancellationToken cancellationToken);
}
