using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Google;

/// <summary>Where a live value came from. The order of the members is not the order of precedence.</summary>
public enum GoogleCredentialSource
{
    /// <summary>Nothing supplies it, from either side.</summary>
    None,

    /// <summary>Typed into the accounts screen and kept by <see cref="IGoogleOAuthClientStore"/>.</summary>
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
/// where each part came from, and every client the panel is holding underneath.
/// </summary>
/// <param name="Stored">
/// The stored client the resolution above drew on — the default one — or null when nothing is
/// stored. It is what the setup panel's own state rows are about.
/// </param>
/// <param name="StoredClients">Every stored client, oldest first.</param>
public sealed record GoogleOAuthCredentialState(
    GoogleCredentialValue ClientId,
    GoogleCredentialSource ClientSecretSource,
    GoogleCredentialValue RedirectUri,
    StoredGoogleOAuthClient? Stored,
    IReadOnlyList<StoredGoogleOAuthClient> StoredClients)
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
/// Reading and writing the OAuth clients from the panel. The rest of the application keeps asking
/// for <see cref="IOptions{TOptions}"/> of <see cref="GoogleOAuthOptions"/> and never learns that
/// any of this happened.
/// </summary>
public interface IGoogleOAuthCredentials
{
    /// <summary>
    /// The client a new consent flow runs with, resolved now. Incomplete rather than null when
    /// something is missing — <see cref="GoogleOAuthOptions.IsConfigured"/> is how callers ask, and
    /// the panel has to render the incomplete state rather than crash on it.
    /// </summary>
    GoogleOAuthOptions InForce { get; }

    GoogleOAuthCredentialState Describe();

    /// <summary>
    /// The complete client for one of Google's client ids, or null when this panel cannot produce a
    /// secret for it.
    ///
    /// <para>This is the method the multi-client story rests on. A refresh token is issued to a
    /// client and can only be presented by that client — Google answers anything else with
    /// <c>invalid_grant</c>, which this codebase turns into "reconnect this account". So a refresh
    /// must not ask "what is in force", it must ask "what connected this account", and that is this
    /// call. Getting it wrong looks like working multi-client support until the first hour
    /// elapses.</para>
    /// </summary>
    GoogleOAuthOptions? ForClientId(string clientId);

    /// <summary>
    /// Saves what the operator typed — a new client when <paramref name="id"/> is null, an edit of
    /// that one when it is not. A null or empty <paramref name="clientSecret"/> keeps the one already
    /// stored: the form cannot show a secret back, so it cannot ask for it again either.
    /// </summary>
    GoogleOAuthClientSaveResult Save(Guid? id, string clientId, string? clientSecret, string redirectUri);

    /// <summary>Makes one stored client the one new connections run with.</summary>
    bool MakeDefault(Guid id);

    GoogleOAuthClientRemovalResult Remove(Guid id);
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
/// find. The panel is the fallback for a deployment that supplies nothing.</para>
///
/// <para><b>How the two coexist now that there can be several stored clients.</b> Configuration
/// still supplies at most one client and still wins, so the answer to "what does the next connection
/// use" is unchanged. What the stored rows add is the ability to <em>refresh</em> an account
/// connected under a different client — <see cref="ForClientId"/> — and that lookup deliberately
/// checks the configured client first, so a stored row that happens to carry the same client id
/// cannot shadow it. Nothing about a stored row changes what is in force, and nothing about
/// configuration deletes a stored row: removing the environment variable brings the panel's own
/// client back rather than leaving the deployment with nothing.</para>
///
/// <para>Blank counts as absent, and that is load-bearing: <c>appsettings.Development.json</c> ships
/// <c>"ClientId": ""</c> as documentation of the key's existence. Treating a present-but-empty key
/// as configured would make the development machine permanently unable to save anything.</para>
///
/// <para><b>Why this is an <see cref="IOptions{TOptions}"/> and not a binding.</b> The values arrive
/// after the container is built — the operator types them into a running panel — and a bound
/// options instance is computed once and cached for the life of the process. Resolving on every
/// read is what makes «save» take effect on the next request instead of the next restart.</para>
/// </summary>
public sealed class GoogleOAuthCredentialResolver : IOptions<GoogleOAuthOptions>, IGoogleOAuthCredentials
{
    private readonly IConfiguration _section;
    private readonly IGoogleOAuthClientStore _store;

    /// <param name="section">The <c>Google</c> configuration section, not the root.</param>
    public GoogleOAuthCredentialResolver(IConfiguration section, IGoogleOAuthClientStore store)
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
            // The stored client is fetched only if configuration leaves a field for it to fill. A
            // deployment that supplies all three from its environment — which is the live one — never
            // touches the clients table to answer this, and the token refresh is on this path.
            var stored = new Lazy<StoredGoogleOAuthClient?>(_store.Default);

            return new GoogleOAuthOptions
            {
                ClientId = Resolve(nameof(GoogleOAuthOptions.ClientId), stored, c => c.ClientId).Value,
                ClientSecret = ResolveSecret(stored) ?? string.Empty,
                RedirectUri = Resolve(nameof(GoogleOAuthOptions.RedirectUri), stored, c => c.RedirectUri).Value,
            };
        }
    }

    /// <summary>
    /// The same answer as <see cref="Value"/>, named for readers who are not thinking about the
    /// options pipeline. Every consumer of <see cref="IGoogleOAuthCredentials"/> resolves the same
    /// singleton, so the two cannot disagree.
    /// </summary>
    public GoogleOAuthOptions InForce => Value;

    public GoogleOAuthCredentialState Describe()
    {
        var clients = _store.List();

        // Already in hand, so the laziness below costs nothing here — the screen was always going to
        // read the whole list.
        var stored = new Lazy<StoredGoogleOAuthClient?>(
            () => clients.FirstOrDefault(c => c.IsDefault) ?? clients.FirstOrDefault());

        return new GoogleOAuthCredentialState(
            Resolve(nameof(GoogleOAuthOptions.ClientId), stored, c => c.ClientId),
            SecretSource(stored),
            Resolve(nameof(GoogleOAuthOptions.RedirectUri), stored, c => c.RedirectUri),
            stored.Value,
            clients);
    }

    public GoogleOAuthOptions? ForClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var wanted = clientId.Trim();

        // Configuration first, so a stored row carrying the same client id cannot shadow the
        // deployment's own. If the configured client is only half supplied — an id in the
        // environment and the secret in the panel — this still answers with what the resolution
        // above produces, which is the client that would actually have been sent to Google.
        var inForce = Value;
        if (inForce.IsConfigured() && string.Equals(inForce.ClientId, wanted, StringComparison.Ordinal))
        {
            return inForce;
        }

        if (_store.FindByClientId(wanted) is not { } stored) return null;

        var secret = _store.ReadSecret(stored.Id);
        if (secret is null) return null;

        var options = new GoogleOAuthOptions
        {
            ClientId = stored.ClientId,
            ClientSecret = secret,

            // Carried for completeness rather than for the refresh, which sends no redirect_uri at
            // all — Google only compares one on the authorization-code exchange.
            RedirectUri = stored.RedirectUri,
        };

        return options.IsConfigured() ? options : null;
    }

    public GoogleOAuthClientSaveResult Save(
        Guid? id,
        string clientId,
        string? clientSecret,
        string redirectUri) =>
        _store.Save(id, clientId, clientSecret, redirectUri);

    public bool MakeDefault(Guid id) => _store.MakeDefault(id);

    public GoogleOAuthClientRemovalResult Remove(Guid id) => _store.Remove(id);

    /// <summary>
    /// Configuration first, the panel second, and whitespace on either side of either one trimmed.
    /// A client id pasted with a trailing newline — out of a terminal, out of an env file, out of
    /// Google's own console — is otherwise a <c>redirect_uri_mismatch</c> that shows nothing wrong
    /// on screen.
    /// </summary>
    private GoogleCredentialValue Resolve(
        string key,
        Lazy<StoredGoogleOAuthClient?> stored,
        Func<StoredGoogleOAuthClient, string> fromPanel)
    {
        if (_section[key] is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            return new GoogleCredentialValue(configured.Trim(), GoogleCredentialSource.Configuration);
        }

        return stored.Value is { } client && fromPanel(client) is { } value
            && !string.IsNullOrWhiteSpace(value)
                ? new GoogleCredentialValue(value.Trim(), GoogleCredentialSource.Panel)
                : GoogleCredentialValue.Unset;
    }

    private string? ResolveSecret(Lazy<StoredGoogleOAuthClient?> stored)
    {
        if (_section[nameof(GoogleOAuthOptions.ClientSecret)] is { } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return stored.Value is { } client ? _store.ReadSecret(client.Id) : null;
    }

    /// <summary>
    /// Where the secret in force comes from, without going near its value. A stored secret that no
    /// longer decrypts is reported as <see cref="GoogleCredentialSource.None"/>, because that is
    /// what it is worth.
    /// </summary>
    private GoogleCredentialSource SecretSource(Lazy<StoredGoogleOAuthClient?> stored)
    {
        if (_section[nameof(GoogleOAuthOptions.ClientSecret)] is { } configured
            && !string.IsNullOrWhiteSpace(configured))
        {
            return GoogleCredentialSource.Configuration;
        }

        return stored.Value is { HasClientSecret: true }
            ? GoogleCredentialSource.Panel
            : GoogleCredentialSource.None;
    }
}
