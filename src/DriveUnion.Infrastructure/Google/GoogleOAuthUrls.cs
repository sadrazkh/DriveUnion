using Microsoft.AspNetCore.WebUtilities;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The two Google endpoints the operator's consent flow uses, and the query it has to carry.
///
/// This sits next to the token exchange on purpose: <c>access_type=offline</c> and the
/// <c>prompt</c> values below are what decide whether Google returns a refresh token at all, and
/// which account it returns one for. Neither answer fails here — a missing refresh token fails an
/// hour later as an account that cannot be refreshed, and a missing account chooser fails as a
/// second Connect that quietly reconnects the first account.
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
    /// Both <c>prompt</c> values, space separated the way OAuth 2.0 specifies a list of them.
    /// Neither one substitutes for the other and dropping either has a distinct, delayed cost.
    ///
    /// <para><c>select_account</c> is what makes a second account reachable at all. Without it Google
    /// silently reuses whichever account the browser session is already signed into and never offers
    /// the chooser — so the operator presses «افزودن اکانت» a second time, approves what looks like a
    /// fresh consent, and lands back on a panel that still shows one account. From the panel that is
    /// indistinguishable from "this product only supports one account", which is exactly how it was
    /// reported.</para>
    ///
    /// <para><c>consent</c> has to stay beside it. Google issues a refresh token only on a fresh
    /// grant, so an account re-approved without it comes back with an access token and nothing to
    /// renew it: it works for an hour and then cannot be refreshed. That failure surfaces long after
    /// the screen that caused it, which is why the value is pinned by a test rather than left to
    /// whoever edits this dictionary next.</para>
    /// </summary>
    public const string Prompt = "select_account consent";

    /// <summary>
    /// Where the operator is sent to approve an account.
    ///
    /// <c>access_type=offline</c> asks for a refresh token and <see cref="Prompt"/> is what makes
    /// Google actually issue one — and ask which account it belongs to.
    /// </summary>
    /// <param name="loginHint">
    /// The address of the account this consent is meant to re-approve, or null when the operator is
    /// adding one and no account is meant yet.
    ///
    /// A hint and nothing more: Google is documented to use it to preselect an account, and it is
    /// not a constraint on what comes back. The chooser still appears because <see cref="Prompt"/>
    /// still asks for it, so an operator who reconnects A2 and is shown A1 preselected can see that
    /// and pick again — and if they approve the wrong one anyway, the callback stores it against
    /// whatever address Drive reports, which is a correct row for the account that was actually
    /// approved rather than a corrupted one for the account that was not. Whether Google honours a
    /// hint underneath <c>select_account</c> could not be verified from here; nothing depends on it
    /// being honoured.
    /// </param>
    public static string BuildAuthorizationUrl(
        GoogleOAuthOptions options,
        string state,
        string? loginHint = null)
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
            ["prompt"] = Prompt,
            ["include_granted_scopes"] = "true",
            ["state"] = state,
        };

        // Omitted rather than sent empty: `login_hint=` is a hint that names nothing, and Google's
        // handling of one is not something to find out on the operator's screen.
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            query["login_hint"] = loginHint;
        }

        return QueryHelpers.AddQueryString(AuthorizationEndpoint, query);
    }
}
