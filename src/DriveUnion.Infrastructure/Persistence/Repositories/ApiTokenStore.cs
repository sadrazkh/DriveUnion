using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The customer's API keys.
/// </summary>
public sealed class ApiTokenStore(DriveUnionDbContext db, TimeProvider clock) : IApiTokens
{
    /// <summary>
    /// How stale <c>LastUsedAt</c> may get before it is written again.
    ///
    /// <para>This runs on the authentication path, so writing it per request would be an UPDATE on
    /// every call of every key — for a column whose only reader is a person deciding whether a key
    /// is still in use, to whom «today» and «this hour» are the same answer.</para>
    /// </summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<ApiTokenSummary>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tokens = await db.ApiTokens
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new ApiTokenSummary(
                t.Id, t.Name, t.Prefix, t.Scope, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.RevokedAt))
            .ToListAsync(cancellationToken);

        // Sorted in memory, like every other DateTimeOffset ordering in this layer: SQLite's TEXT
        // encoding does not sort correctly once two rows carry different offsets.
        return [.. tokens.OrderByDescending(t => t.CreatedAt)];
    }

    public async Task<ApiTokenResult> MintAsync(
        Guid tenantId,
        Guid ownerUserId,
        string name,
        ApiScope scope,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        if (Clean(name) is not { } cleaned) return new ApiTokenResult(ApiTokenOutcome.NameEmpty);

        var now = clock.GetUtcNow();

        // Revoked and expired keys do not count. They are kept so «this key was revoked» outlives
        // the key, and a ceiling that counted history would eventually refuse a workspace that has
        // none in use.
        //
        // The expiry is judged in memory, and it has to be: `t.ExpiresAt > now` is a DateTimeOffset
        // comparison in SQL, and SQLite refuses those — the same wall FileCatalog's ordering and the
        // whole of P9's roll-up were built around. The revoked half filters in SQL because a null
        // check is not a comparison, so what comes back is at most a workspace's keys.
        var expiries = await db.ApiTokens
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.RevokedAt == null)
            .Select(t => t.ExpiresAt)
            .ToListAsync(cancellationToken);

        var live = expiries.Count(e => e is null || e > now);

        if (live >= ApiToken.MaxPerTenant) return new ApiTokenResult(ApiTokenOutcome.TooMany);

        var secret = NewSecret();
        var prefix = secret[ApiToken.Marker.Length..(ApiToken.Marker.Length + ApiToken.PrefixLength)];

        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Name = cleaned,
            Prefix = prefix,
            SecretHash = Hash(secret),
            Scope = scope,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new ApiTokenResult(
            ApiTokenOutcome.Done,
            new MintedApiToken(
                new ApiTokenSummary(
                    token.Id, token.Name, token.Prefix, token.Scope, token.CreatedAt, null, token.ExpiresAt, null),
                secret));
    }

    public async Task<ApiTokenResult> RevokeAsync(
        Guid tenantId,
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        // Stamped rather than deleted, and only if it is not stamped already — revoking twice must
        // not move the moment it happened.
        var affected = await db.ApiTokens
            .Where(t => t.Id == tokenId && t.TenantId == tenantId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, clock.GetUtcNow()),
                cancellationToken);

        return affected == 0
            ? new ApiTokenResult(ApiTokenOutcome.NotFound)
            : new ApiTokenResult(ApiTokenOutcome.Done);
    }

    public async Task<ApiCaller?> AuthenticateAsync(string presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;
        if (!presented.StartsWith(ApiToken.Marker, StringComparison.Ordinal)) return null;
        if (presented.Length < ApiToken.Marker.Length + ApiToken.PrefixLength) return null;

        var prefix = presented[ApiToken.Marker.Length..(ApiToken.Marker.Length + ApiToken.PrefixLength)];
        var now = clock.GetUtcNow();

        // The prefix narrows and the hash decides. Looking a token up by its hash directly would
        // work too and would be one index; the prefix is what lets the panel show somebody which of
        // their keys a row is, which a hash cannot.
        var candidates = await db.ApiTokens
            .AsNoTracking()
            .Where(t => t.Prefix == prefix)
            .Select(t => new
            {
                t.Id,
                t.TenantId,
                t.OwnerUserId,
                t.Scope,
                t.SecretHash,
                t.RevokedAt,
                t.ExpiresAt,
                t.LastUsedAt,
            })
            .ToListAsync(cancellationToken);

        var offered = Hash(presented);

        foreach (var candidate in candidates)
        {
            // Fixed-time, so a caller cannot learn a hash a byte at a time from how long the answer
            // took. The prefix above is compared in SQL and is public by design; this is the half
            // that is a secret.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(candidate.SecretHash),
                    Encoding.UTF8.GetBytes(offered)))
            {
                continue;
            }

            // Revoked and expired answer the same «no» as unknown — see IApiTokens.AuthenticateAsync
            // for why they are not told apart.
            if (candidate.RevokedAt is not null) return null;
            if (candidate.ExpiresAt is { } end && now >= end) return null;

            if (candidate.LastUsedAt is null || now - candidate.LastUsedAt.Value >= LastUsedResolution)
            {
                await db.ApiTokens
                    .Where(t => t.Id == candidate.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), cancellationToken);
            }

            return new ApiCaller(candidate.Id, candidate.TenantId, candidate.OwnerUserId, candidate.Scope);
        }

        return null;
    }

    /// <summary>
    /// <c>du_</c> and 32 bytes of base64url.
    ///
    /// <para>Base64url rather than base64 so the whole token is safe in a header, a URL and a shell
    /// argument without quoting — the three places a customer will paste it.</para>
    /// </summary>
    private static string NewSecret() =>
        ApiToken.Marker + Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string presented) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length > ApiToken.MaxNameLength ? trimmed[..ApiToken.MaxNameLength] : trimmed;
    }
}

/// <summary>Base64 without the three characters that need escaping somewhere.</summary>
internal static class Base64Url
{
    internal static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
