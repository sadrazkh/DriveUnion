namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The operator's Google OAuth client. Bound from the <c>Google</c> configuration section.
///
/// Consumer Google One accounts cannot be reached with a service account — domain-wide delegation
/// needs Workspace — so this is the ordinary three-legged user flow, run twice ever, by the operator
/// alone. Customers never see a Google consent screen; that is what keeps OAuth verification and the
/// CASA assessment off the critical path.
/// </summary>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Secret. Comes from user-secrets or the environment, never from a committed file.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Must match one of the authorised redirect URIs on the OAuth client exactly, including scheme,
    /// port and trailing slash — Google compares the string, not the address.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(RedirectUri);
}
