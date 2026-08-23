using System.Collections.Concurrent;
using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<GoogleOAuthOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleTokenService> _logger;

    public GoogleTokenService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ITokenProtector protector,
        IOptions<GoogleOAuthOptions> options,
        TimeProvider timeProvider,
        ILogger<GoogleTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _protector = protector;
        _options = options;
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
        var options = RequireOptions();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        var account = await db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DriveAccountUnavailableException($"Google account {accountId} is not in the pool.");

        var refreshToken = _protector.Unprotect(account.RefreshTokenProtected);
        if (refreshToken is null)
        {
            await MarkDisconnectedAsync(db, account, cancellationToken).ConfigureAwait(false);
            throw new DriveAccountUnavailableException(
                $"The stored refresh token for {account.Email} cannot be decrypted. The Data "
                + "Protection key that wrote it is gone, and the account has to be reconnected.");
        }

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
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveAccountUnavailableException)
        {
            // invalid_grant: revoked, or expired because the consent screen is still in Testing,
            // where Google expires refresh tokens after seven days.
            await MarkDisconnectedAsync(db, account, cancellationToken).ConfigureAwait(false);
            throw;
        }

        account.AccessTokenProtected = _protector.Protect(grant.AccessToken);
        account.AccessTokenExpiresAt = grant.ExpiresAt;

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

    private static async Task MarkDisconnectedAsync(
        DriveUnionDbContext db,
        GoogleAccount account,
        CancellationToken cancellationToken)
    {
        account.Status = GoogleAccountStatus.Disconnected;
        account.AccessTokenProtected = null;
        account.AccessTokenExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleTokenGrant> PostAsync(
        Dictionary<string, string> form,
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

        return ParseGrant(body, _timeProvider.GetUtcNow());
    }

    private static GoogleTokenGrant ParseGrant(string body, DateTimeOffset now)
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
            now + lifetime);
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

    private GoogleOAuthOptions RequireOptions()
    {
        var options = _options.Value;
        if (!options.IsConfigured())
        {
            // Deliberately not a startup check. The panel has to boot without Google credentials —
            // that is the state this product is developed in — and the first honest place to say so
            // is the first request that actually needs them.
            throw new DriveAccountUnavailableException(
                "Google OAuth is not configured. Set Google:ClientId, Google:ClientSecret and "
                + "Google:RedirectUri before connecting or refreshing an account.");
        }

        return options;
    }
}
