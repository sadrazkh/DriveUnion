using System.Security.Cryptography;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The customer's S3 access keys, with the secret encrypted rather than hashed.
///
/// <para>The reason is on <see cref="S3Credential"/> and is worth repeating where the code is: SigV4
/// verification recomputes the client's HMAC chain, which needs the secret. There is no variant of
/// the protocol a one-way hash satisfies.</para>
/// </summary>
public sealed class S3CredentialStore(
    DriveUnionDbContext db,
    ITokenProtector protector,
    TimeProvider clock) : IS3Credentials
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromHours(1);

    /// <summary>Upper-case base32, so an access key id looks like one and survives any config file.</summary>
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public async Task<IReadOnlyList<S3CredentialSummary>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await db.S3Credentials
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new S3CredentialSummary(
                c.Id, c.Name, c.AccessKeyId, c.Scope, c.CreatedAt, c.LastUsedAt, c.RevokedAt))
            .ToListAsync(cancellationToken);

        return [.. rows.OrderByDescending(c => c.CreatedAt)];
    }

    public async Task<S3CredentialResult> MintAsync(
        Guid tenantId,
        Guid ownerUserId,
        string name,
        ApiScope scope,
        CancellationToken cancellationToken)
    {
        var cleaned = name?.Trim();

        if (string.IsNullOrEmpty(cleaned)) return new S3CredentialResult(ApiTokenOutcome.NameEmpty);
        if (cleaned.Length > S3Credential.MaxNameLength) cleaned = cleaned[..S3Credential.MaxNameLength];

        var live = await db.S3Credentials
            .CountAsync(c => c.TenantId == tenantId && c.RevokedAt == null, cancellationToken);

        if (live >= S3Credential.MaxPerTenant) return new S3CredentialResult(ApiTokenOutcome.TooMany);

        var accessKeyId = S3Credential.Marker + RandomBase32(S3Credential.AccessKeyIdLength - S3Credential.Marker.Length);
        var secret = Base64Url.Encode(RandomNumberGenerator.GetBytes(30))[..S3Credential.SecretLength];

        var credential = new S3Credential
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Name = cleaned,
            AccessKeyId = accessKeyId,
            SecretProtected = protector.Protect(secret),
            Scope = scope,
            CreatedAt = clock.GetUtcNow(),
        };

        db.S3Credentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);

        return new S3CredentialResult(
            ApiTokenOutcome.Done,
            new MintedS3Credential(
                new S3CredentialSummary(
                    credential.Id, credential.Name, accessKeyId, scope, credential.CreatedAt, null, null),
                secret));
    }

    public async Task<S3CredentialResult> RevokeAsync(
        Guid tenantId,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var affected = await db.S3Credentials
            .Where(c => c.Id == credentialId && c.TenantId == tenantId && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, clock.GetUtcNow()), cancellationToken);

        return affected == 0
            ? new S3CredentialResult(ApiTokenOutcome.NotFound)
            : new S3CredentialResult(ApiTokenOutcome.Done);
    }

    public async Task<S3Signer?> ResolveAsync(string accessKeyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId)) return null;

        var row = await db.S3Credentials
            .AsNoTracking()
            .Where(c => c.AccessKeyId == accessKeyId)
            .Select(c => new
            {
                c.Id,
                c.TenantId,
                c.OwnerUserId,
                c.Scope,
                c.SecretProtected,
                c.RevokedAt,
                c.LastUsedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Unknown and revoked answer alike — see IS3Credentials.ResolveAsync. The access key id
        // travels in the clear on every request, so confirming one is confirming half the pair.
        if (row is null || row.RevokedAt is not null) return null;

        // A key ring that has rotated past this row leaves a secret nobody can read. Null rather
        // than a throw: the gateway answers «that signature does not match», which is true, and the
        // customer's remedy — mint another — is the same either way.
        if (protector.Unprotect(row.SecretProtected) is not { } secret) return null;

        var now = clock.GetUtcNow();

        if (row.LastUsedAt is null || now - row.LastUsedAt.Value >= LastUsedResolution)
        {
            await db.S3Credentials
                .Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), cancellationToken);
        }

        return new S3Signer(row.Id, row.TenantId, row.OwnerUserId, row.Scope, secret);
    }

    private static string RandomBase32(int length)
    {
        var chars = new char[length];
        var bytes = RandomNumberGenerator.GetBytes(length);

        for (var i = 0; i < length; i++) chars[i] = KeyAlphabet[bytes[i] % KeyAlphabet.Length];

        return new string(chars);
    }
}
