namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// What Google handed back from the token endpoint. The refresh token is present only on the
/// authorization-code exchange — a refresh grant reuses the one already stored.
/// </summary>
/// <param name="ClientId">
/// The OAuth client this grant was obtained with, carried out because the caller has to store it.
///
/// A refresh token can only be presented by the client that issued it, so an account has to record
/// which one that was — and it has to be the client the exchange actually used, not whatever is in
/// force by the time the row is written. If the operator promotes a different client while the
/// consent window is open, the exchange fails at Google and no account is written at all, which is
/// the right outcome and a much better one than a row bound to a client that never saw it.
/// </param>
public sealed record GoogleTokenGrant(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string ClientId);

/// <summary>
/// Owns every access token in the process.
///
/// Nothing above this holds a credential, and nothing else may call Google's token endpoint: the
/// single-flight in the implementation only works if every path to a refresh goes through here.
/// </summary>
public interface IGoogleTokenService
{
    /// <summary>
    /// A usable access token for the account, refreshing it first if it is missing or about to
    /// expire. Twenty concurrent chunk uploads against the same account produce one call to Google.
    /// </summary>
    /// <exception cref="Core.Abstractions.DriveAccountUnavailableException">
    /// The account cannot be refreshed and the operator has to reconnect it.
    /// </exception>
    Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Trades the operator's consent for tokens. No account row exists yet at this point.</summary>
    Task<GoogleTokenGrant> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string redirectUri,
        CancellationToken cancellationToken);
}
