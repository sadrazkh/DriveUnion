using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Google;

/// <summary>Where a live value came from. The order of the members is not the order of precedence.</summary>
public enum GoogleCredentialSource
{
    /// <summary>Nothing supplies it, from either side.</summary>
    None,

    /// <summary>Typed into the accounts screen and kept by <see cref="IGoogleOAuthCredentialStore"/>.</summary>
    Panel,

    /// <summary>The <c>Google</c> configuration section: environment, user-secrets or appsettings.</summary>
    Configuration,
}

/// <summary>One resolved setting and the reason it has the value it has.</summary>
public readonly record struct GoogleCredentialValue(string Value, GoogleCredentialSource Source)
{
    public static GoogleCredentialValue Unset => new(string.Empty, GoogleCredentialSource.None);

    public bool IsSet => Source is not GoogleCredentialSource.None;
}

/// <summary>
/// The whole credential picture, as the operator's screen needs to explain it: what is in force,
/// where each part came from, and what the panel is holding underneath.
/// </summary>
public sealed record GoogleOAuthCredentialState(
    GoogleCredentialValue ClientId,
    GoogleCredentialSource ClientSecretSource,
    GoogleCredentialValue RedirectUri,
    StoredGoogleOAuthClient? Stored)
{
    public bool IsComplete =>
        ClientId.IsSet
        && ClientSecretSource is not GoogleCredentialSource.None
        && RedirectUri.IsSet;

    /// <summary>
    /// True when the operator has saved something the environment is quietly outranking. The screen
    /// says so out loud, because the alternative is an operator staring at the client id they typed
    /// while Google is being sent a different one.
    /// </summary>
    public bool ConfigurationOutranksPanel =>
        Stored is not null
        && (ClientId.Source is GoogleCredentialSource.Configuration
            || RedirectUri.Source is GoogleCredentialSource.Configuration
            || (Stored.HasClientSecret && ClientSecretSource is GoogleCredentialSource.Configuration));
}

/// <summary>
/// Reading and writing the OAuth client from the panel. The rest of the application keeps asking
/// for <see cref="IOptions{TOptions}"/> of <see cref="GoogleOAuthOptions"/> and never learns that
/// any of this happened.
/// </summary>
public interface IGoogleOAuthCredentials
{
    GoogleOAuthCredentialState Describe();

    /// <summary>
    /// Saves what the operator typed. A null or empty <paramref name="clientSecret"/> keeps the one
    /// already stored — the form cannot show a secret back, so it cannot ask for it again either.
    /// </summary>
    void Save(string clientId, string? clientSecret, string redirectUri);

    bool Clear();
}

/// <summary>
/// The one place that decides which Google client is in force.
///
/// <para><b>Precedence: configuration wins, field by field.</b> A non-blank <c>Google:ClientId</c>,
/// <c>Google:ClientSecret</c> or <c>Google:RedirectUri</c> — from the environment, from
/// user-secrets, from appsettings — outranks whatever the panel has stored under the same name.
/// The reason is that the environment is the deployment's statement of record: it is what a
/// pipeline sets, what an audit reads, and what somebody will change at three in the morning
/// expecting it to take effect. A form on a web page silently beating an environment variable would
/// make that variable look broken, and the box would be sending Google a client id nobody could
/// find. The panel is the fallback for a deployment that supplies nothing, which is every
/// deployment this product actually has.</para>
///
/// <para>Blank counts as absent, and that is load-bearing: <c>appsettings.Development.json</c> ships
/// <c>"ClientId": ""</c> as documentation of the key's existence. Treating a present-but-empty key
/// as configured would make the development machine permanently unable to save anything.</para>
///
/// <para><b>Why this is an <see cref="IOptions{TOptions}"/> and not a binding.</b> The values arrive
/// after the container is built — the operator types them into a running panel — and a bound
/// options instance is computed once and cached for the life of the process. Resolving on every
/// read is what makes «save» take effect on the next request instead of the next restart. The reads
/// are a configuration lookup and a cached file, so there is nothing here worth caching and no
/// staleness worth debugging.</para>
/// </summary>
public sealed class GoogleOAuthCredentialResolver : IOptions<GoogleOAuthOptions>, IGoogleOAuthCredentials
{
    private readonly IConfiguration _section;
    private readonly IGoogleOAuthCredentialStore _store;

    /// <param name="section">The <c>Google</c> configuration section, not the root.</param>
    public GoogleOAuthCredentialResolver(IConfiguration section, IGoogleOAuthCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(store);

        _section = section;
        _store = store;
    }

    /// <summary>
    /// The client in force, resolved now.
    ///
    /// It never throws: an incomplete client is a state the panel has to render rather than an
    /// error, and <see cref="GoogleOAuthOptions.IsConfigured"/> is how every caller asks. The one
    /// place that turns "not configured" into an exception is
    /// <see cref="GoogleTokenService"/>, at the moment a request actually needs to reach Google,
    /// where it can name the three settings that are missing.
    /// </summary>
    public GoogleOAuthOptions Value
    {
        get
        {
            var stored = _store.Read();

            return new GoogleOAuthOptions
            {
                ClientId = Resolve(nameof(GoogleOAuthOptions.ClientId), stored?.ClientId).Value,
                ClientSecret = ResolveSecret() ?? string.Empty,
                RedirectUri = Resolve(nameof(GoogleOAuthOptions.RedirectUri), stored?.RedirectUri).Value,
            };
        }
    }

    public GoogleOAuthCredentialState Describe()
    {
        var stored = _store.Read();

        return new GoogleOAuthCredentialState(
            Resolve(nameof(GoogleOAuthOptions.ClientId), stored?.ClientId),
            SecretSource(stored),
            Resolve(nameof(GoogleOAuthOptions.RedirectUri), stored?.RedirectUri),
            stored);
    }

    public void Save(string clientId, string? clientSecret, string redirectUri) =>
        _store.Save(clientId, clientSecret, redirectUri);

    public bool Clear() => _store.Clear();

    /// <summary>
    /// Configuration first, the panel second, and whitespace on either side of either one trimmed.
    /// A client id pasted with a trailing newline — out of a terminal, out of an env file, out of
    /// Google's own console — is otherwise a <c>redirect_uri_mismatch</c> that shows nothing wrong
    /// on screen.
    /// </summary>
    private GoogleCredentialValue Resolve(string key, string? stored)
    {
        if (_section[key] is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            return new GoogleCredentialValue(configured.Trim(), GoogleCredentialSource.Configuration);
        }

        return string.IsNullOrWhiteSpace(stored)
            ? GoogleCredentialValue.Unset
            : new GoogleCredentialValue(stored.Trim(), GoogleCredentialSource.Panel);
    }

    private string? ResolveSecret()
    {
        if (_section[nameof(GoogleOAuthOptions.ClientSecret)] is { } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return _store.ReadClientSecret();
    }

    /// <summary>
    /// Where the secret in force comes from, without going near its value. A stored secret that no
    /// longer decrypts is reported as <see cref="GoogleCredentialSource.None"/>, because that is
    /// what it is worth.
    /// </summary>
    private GoogleCredentialSource SecretSource(StoredGoogleOAuthClient? stored)
    {
        if (_section[nameof(GoogleOAuthOptions.ClientSecret)] is { } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return GoogleCredentialSource.Configuration;
        }

        return stored is { HasClientSecret: true }
            ? GoogleCredentialSource.Panel
            : GoogleCredentialSource.None;
    }
}
