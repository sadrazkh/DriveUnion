namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The operator's Google OAuth client, as it is in force right now.
///
/// Consumer Google One accounts cannot be reached with a service account — domain-wide delegation
/// needs Workspace — so this is the ordinary three-legged user flow, run twice ever, by the operator
/// alone. Customers never see a Google consent screen; that is what keeps OAuth verification and the
/// CASA assessment off the critical path.
///
/// Two sources fill this in: the <c>Google</c> configuration section, and the accounts screen.
/// Configuration wins field by field — <see cref="GoogleOAuthCredentialResolver"/> is where that is
/// decided and argued. This type is a snapshot of the answer, not a binding: it is rebuilt on every
/// read, because the panel's half of it can change while the process is running.
/// </summary>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Secret. From user-secrets, the environment, or the accounts screen — never from a committed
    /// file, and never rendered back to a browser once it has been saved.
    /// </summary>
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
