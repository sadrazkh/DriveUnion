using System.Collections.Concurrent;
using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// Google access tokens live about an hour; refresh tokens live until they are revoked. This turns
/// the second into the first, once at a time per account.
///
/// A singleton, because the single-flight gate below has to be the same object for every request in
/// the process. It therefore opens its own scope for each database touch rather than holding a
/// <see cref="DriveUnionDbContext"/>, which also removes the change-tracker staleness that would
/// otherwise make the double-check after the gate read its own old copy of the row.
/// </summary>
public sealed class GoogleTokenService : IGoogleTokenService
{
    public const string HttpClientName = "DriveUnion.Google.OAuth";

    /// <summary>
    /// One gate per account. The dictionary is unbounded in principle and bounded in practice by the
    /// size of the operator's pool, which is two.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITokenProtector _protector;
    private readonly IGoogleOAuthCredentials _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleTokenService> _logger;

    public GoogleTokenService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ITokenProtector protector,
        IGoogleOAuthCredentials credentials,
        TimeProvider timeProvider,
        ILogger<GoogleTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _protector = protector;
        _credentials = credentials;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var cached = await ReadUsableAccessTokenAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var gate = _gates.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The second read is the whole point. Nineteen of the twenty chunk uploads that piled up
            // behind this gate find the token the twentieth already fetched and persisted, and Google
            // sees one refresh instead of twenty.
            cached = await ReadUsableAccessTokenAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            return await RefreshAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<GoogleTokenGrant> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var options = RequireOptions();

        var grant = await PostAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = authorizationCode,
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            },
            options.ClientId,
            cancellationToken).ConfigureAwait(false);

        if (grant.RefreshToken is null)
        {
            // Google returns a refresh token only on a fresh grant. Without one the account works
            // for an hour and then cannot be refreshed, so failing here — loudly, at the moment the
            // operator is looking at the screen — is much better than failing then.
            throw new DriveAccountUnavailableException(
                "Google returned no refresh token for this authorization code. The consent URL must "
                + "carry access_type=offline and prompt=consent; without a refresh token the account "
                + "stops working an hour after it is connected.");
        }

        return grant;
    }

    private async Task<string?> ReadUsableAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        var account = await db.GoogleAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            throw new DriveAccountUnavailableException(
                $"Google account {accountId} is not in the pool.");
        }

        if (account.NeedsAccessToken(_timeProvider.GetUtcNow()))
        {
            return null;
        }

        // A cached token that will not decrypt is the same problem as no token: refresh, which will
        // report the account properly if the refresh token is unreadable too.
        return _protector.Unprotect(account.AccessTokenProtected!);
    }

    private async Task<string> RefreshAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        var account = await db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DriveAccountUnavailableException($"Google account {accountId} is not in the pool.");

        var refreshToken = _protector.Unprotect(account.RefreshTokenProtected);
        if (refreshToken is null)
        {
            var reason =
                $"The stored refresh token for {account.Email} cannot be decrypted. The Data "
                + "Protection key that wrote it is gone, and the account has to be reconnected.";

            await MarkDisconnectedAsync(db, account, reason, cancellationToken).ConfigureAwait(false);
            throw new DriveAccountUnavailableException(reason);
        }

        var options = await RequireClientForAsync(account, db, cancellationToken).ConfigureAwait(false);

        GoogleTokenGrant grant;
        try
        {
            grant = await PostAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token",
                },
                options.ClientId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveAccountUnavailableException exception)
        {
            // invalid_grant: revoked, expired because the consent screen is still in Testing where
            // Google expires refresh tokens after seven days — or presented by the wrong client,
            // which is what the binding above exists to prevent and what this message would say.
            await MarkDisconnectedAsync(db, account, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        catch (DriveApiException exception)
        {
            // Not a disconnection: a 500 from Google, or a network fault, is not the account's
            // fault and the next attempt may well work. It is still the only record the operator
            // will ever get of why an upload failed, so it is written down.
            await RecordFailureAsync(db, account, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        account.AccessTokenProtected = _protector.Protect(grant.AccessToken);
        account.AccessTokenExpiresAt = grant.ExpiresAt;

        // The binding retires its own fallback: an account written before this column existed is
        // stamped with the client that has just refreshed it, so the next refresh asks no questions.
        account.OAuthClientId ??= grant.ClientId;

        // A refresh that worked is the end of whatever the last failure was. Leaving it on the card
        // would have the operator chasing a fault that fixed itself.
        account.LastFailureReason = null;
        account.LastFailureAt = null;

        if (grant.RefreshToken is not null)
        {
            // Refresh grants normally reuse the existing refresh token. Google reserves the right to
            // rotate it, and dropping a rotated one on the floor breaks the account at some
            // unpredictable later hour, so it is stored whenever it appears.
            account.RefreshTokenProtected = _protector.Protect(grant.RefreshToken);
        }

        // The account's Status is deliberately left alone. A successful refresh does not mean the
        // operator wants it back in upload rotation — Paused is a choice, and Disconnected is
        // cleared by reconnecting, not by a background request happening to succeed.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Refreshed the access token for Google account {AccountId}; it expires at {ExpiresAt}.",
            accountId,
            grant.ExpiresAt);

        return grant.AccessToken;
    }

    /// <summary>
    /// The client this account's refresh token was issued to, or a refusal that says which one is
    /// missing.
    ///
    /// <para>Google binds a refresh token to the client that obtained it. Presenting it under
    /// another client id is <c>invalid_grant</c> — the same answer as a revoked token — so a panel
    /// holding two clients and refreshing with "whichever is in force" would disconnect accounts at
    /// random an hour after they were connected, and the screen would blame the consent screen.</para>
    ///
    /// <para>A null binding is the rows written before the column existed. They are refreshed with
    /// the client in force, which is the client that connected them because there was only one, and
    /// the caller stamps the answer onto the row the moment it works.</para>
    /// </summary>
    private async Task<GoogleOAuthOptions> RequireClientForAsync(
        GoogleAccount account,
        DriveUnionDbContext db,
        CancellationToken cancellationToken)
    {
        if (account.OAuthClientId is not { Length: > 0 } clientId)
        {
            var inForce = _credentials.InForce;
            if (inForce.IsConfigured()) return inForce;

            // Nothing is wrong with this account — the deployment has no credentials at all, and
            // disconnecting would make the operator reconnect every account after fixing something
            // that was never the account's fault. The reason goes on the card and the status does not
            // move, so configuring the panel is the whole of the repair.
            await RecordFailureAsync(db, account, Unconfigured, cancellationToken).ConfigureAwait(false);

            throw new DriveAccountUnavailableException(Unconfigured);
        }

        if (_credentials.ForClientId(clientId) is { } bound) return bound;

        // This is the failure the whole change is about, finally said out loud. The client that
        // connected this account is not in the panel any more — deleted, or destroyed with the
        // container the file store used to live in — and no other client can refresh it.
        //
        // Disconnected, unlike the case above, because this account genuinely cannot be reached and
        // leaving it Healthy would keep the upload router choosing it. A pool with a second account
        // on a client that still exists goes on working.
        var reason =
            $"The Google OAuth client this account was connected with ({clientId}) is not "
            + "configured any more, and a refresh token can only be presented by the client that "
            + "issued it. Add that client back on the accounts screen, or reconnect the account "
            + "under one that is there.";

        await MarkDisconnectedAsync(db, account, reason, cancellationToken).ConfigureAwait(false);

        throw new DriveAccountUnavailableException(reason);
    }

    private async Task MarkDisconnectedAsync(
        DriveUnionDbContext db,
        GoogleAccount account,
        string reason,
        CancellationToken cancellationToken)
    {
        account.Status = GoogleAccountStatus.Disconnected;
        account.AccessTokenProtected = null;
        account.AccessTokenExpiresAt = null;
        account.LastFailureReason = Trim(reason);
        account.LastFailureAt = _timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        DriveUnionDbContext db,
        GoogleAccount account,
        string reason,
        CancellationToken cancellationToken)
    {
        account.LastFailureReason = Trim(reason);
        account.LastFailureAt = _timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The column's width. A reason that would not fit is truncated rather than refused —
    /// losing the tail of a diagnostic is better than losing the disconnection it came with.</summary>
    private static string Trim(string reason) =>
        reason.Length <= 1024 ? reason : reason[..1024];

    private async Task<GoogleTokenGrant> PostAsync(
        Dictionary<string, string> form,
        string clientId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, GoogleOAuthUrls.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = GoogleApiError.Parse(body);

            // invalid_grant is the one that is not going to fix itself: the refresh token has been
            // revoked, or expired under the seven-day rule that applies while the consent screen is
            // in Testing status.
            if (error.Reasons.Contains("invalid_grant", StringComparer.OrdinalIgnoreCase))
            {
                throw new DriveAccountUnavailableException(
                    $"Google rejected the grant ({error.Describe()}). The account has to be "
                    + "reconnected. If this happens weekly, the OAuth consent screen is still in "
                    + "Testing publishing status, where refresh tokens expire after seven days.");
            }

            throw new DriveApiException(
                $"Google's token endpoint answered {(int)response.StatusCode}: {error.Describe()}");
        }

        return ParseGrant(body, _timeProvider.GetUtcNow(), clientId);
    }

    private static GoogleTokenGrant ParseGrant(string body, DateTimeOffset now, string clientId)
    {
        using var document = ParseOrThrow(body);
        var root = document.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new DriveApiException("Google's token endpoint returned no access_token.");
        }

        // Google has sent 3600 for years. Defaulting to it rather than failing means an unexpected
        // omission costs one early refresh instead of a dead account.
        var lifetime = root.TryGetProperty("expires_in", out var expires)
            && expires.TryGetInt32(out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromHours(1);

        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;

        return new GoogleTokenGrant(
            accessToken,
            string.IsNullOrEmpty(refreshToken) ? null : refreshToken,
            now + lifetime,
            clientId);
    }

    private static JsonDocument ParseOrThrow(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The body is not logged: on the success path it holds the tokens themselves.
            throw new DriveApiException("Google's token endpoint returned a body that is not JSON.", ex);
        }
    }

    /// <summary>
    /// Deliberately not a startup check. The panel has to boot without Google credentials — that is
    /// the state this product is developed in — and the first honest place to say so is the first
    /// request that actually needs them.
    /// </summary>
    private const string Unconfigured =
        "Google OAuth is not configured. Set Google:ClientId, Google:ClientSecret and "
        + "Google:RedirectUri, or save a client on the accounts screen, before connecting or "
        + "refreshing an account.";

    /// <summary>
    /// The client in force, resolved through the same object the panel writes to — so a client saved
    /// on the screen is in force for the very next request rather than the next restart.
    /// </summary>
    private GoogleOAuthOptions RequireOptions()
    {
        var options = _credentials.InForce;

        return options.IsConfigured()
            ? options
            : throw new DriveAccountUnavailableException(Unconfigured);
    }
}
