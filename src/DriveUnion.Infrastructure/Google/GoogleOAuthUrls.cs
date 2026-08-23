using Microsoft.AspNetCore.WebUtilities;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The two Google endpoints the operator's consent flow uses, and the query it has to carry.
///
/// This sits next to the token exchange on purpose: <c>access_type=offline</c> and
/// <c>prompt=consent</c> are what decide whether Google returns a refresh token at all, and a
/// missing refresh token does not fail here — it fails an hour later, as an account that cannot be
/// refreshed and has to be reconnected by hand.
/// </summary>
public static class GoogleOAuthUrls
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Full Drive access. This is a Google <em>restricted</em> scope; it is affordable here only
    /// because the accounts are the operator's own and no customer ever authenticates.
    /// </summary>
    public const string DriveScope = "https://www.googleapis.com/auth/drive";

    /// <summary>
    /// Where the operator is sent to approve an account.
    ///
    /// <c>access_type=offline</c> asks for a refresh token; <c>prompt=consent</c> forces the consent
    /// screen even when this account has approved before, because Google issues a refresh token only
    /// on a fresh grant. Reconnecting an already-approved account without it returns an access token
    /// and no refresh token, and the account dies quietly an hour later.
    /// </summary>
    public static string BuildAuthorizationUrl(GoogleOAuthOptions options, string state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state,
        };

        return QueryHelpers.AddQueryString(AuthorizationEndpoint, query);
    }
}
