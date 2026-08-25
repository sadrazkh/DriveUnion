using DriveUnion.Core.Api;

namespace DriveUnion.Core.Application;

/// <summary>One key as the panel lists it. There is deliberately no way to get the secret back.</summary>
public sealed record ApiTokenSummary(
    Guid Id,
    string Name,
    string Prefix,
    ApiScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// A newly minted key, and the only moment its secret exists outside the customer's hands.
/// </summary>
/// <param name="Secret">
/// The whole token, to be shown once and never stored. Nothing in this product can produce it again
/// — see <see cref="ApiToken.SecretHash"/>.
/// </param>
public sealed record MintedApiToken(ApiTokenSummary Token, string Secret);

public enum ApiTokenOutcome
{
    Done,
    NotFound,
    NameEmpty,

    /// <summary>Past <see cref="ApiToken.MaxPerTenant"/> live keys.</summary>
    TooMany,
}

public sealed record ApiTokenResult(ApiTokenOutcome Outcome, MintedApiToken? Minted = null)
{
    public bool Succeeded => Outcome == ApiTokenOutcome.Done;
}

/// <summary>
/// Who a presented key is, resolved once per request.
/// </summary>
public sealed record ApiCaller(Guid TokenId, Guid TenantId, Guid OwnerUserId, ApiScope Scope);

/// <summary>
/// The customer's API keys: minting, listing, revoking, and the one lookup the auth handler makes.
///
/// <para><c>tenantId</c> is explicit on every call that manages a key, like everything else here.
/// <see cref="AuthenticateAsync"/> is the exception and has to be: a bearer token arrives with no
/// cookie and no workspace, and resolving it is how the workspace is discovered — the same shape as
/// <c>IPublicLinkReader</c> and <c>ITelegramIdentityReader</c>, and for the same reason.</para>
/// </summary>
public interface IApiTokens
{
    Task<IReadOnlyList<ApiTokenSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<ApiTokenResult> MintAsync(
        Guid tenantId,
        Guid ownerUserId,
        string name,
        ApiScope scope,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    Task<ApiTokenResult> RevokeAsync(Guid tenantId, Guid tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// The presented secret, or null when it is not a live key of anybody's.
    ///
    /// <para>Null for every kind of no — malformed, unknown, revoked, expired — because telling
    /// those apart is how somebody works out that a prefix they guessed is real.</para>
    /// </summary>
    Task<ApiCaller?> AuthenticateAsync(string presented, CancellationToken cancellationToken);
}
