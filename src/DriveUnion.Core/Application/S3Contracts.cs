using DriveUnion.Core.Api;

namespace DriveUnion.Core.Application;

public sealed record S3CredentialSummary(
    Guid Id,
    string Name,
    string AccessKeyId,
    ApiScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <param name="Secret">Shown once. Unlike an API key this one <i>could</i> be shown again; it is not.</param>
public sealed record MintedS3Credential(S3CredentialSummary Credential, string Secret);

public sealed record S3CredentialResult(ApiTokenOutcome Outcome, MintedS3Credential? Minted = null)
{
    public bool Succeeded => Outcome == ApiTokenOutcome.Done;
}

/// <summary>
/// A signing key resolved from an access key id: who it is, and the secret needed to check the
/// signature. <b>Never leaves the gateway</b> — see <see cref="S3Credential"/>.
/// </summary>
public sealed record S3Signer(Guid CredentialId, Guid TenantId, Guid OwnerUserId, ApiScope Scope, string Secret);

/// <summary>
/// The customer's S3 access keys.
/// </summary>
public interface IS3Credentials
{
    Task<IReadOnlyList<S3CredentialSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<S3CredentialResult> MintAsync(
        Guid tenantId,
        Guid ownerUserId,
        string name,
        ApiScope scope,
        CancellationToken cancellationToken);

    Task<S3CredentialResult> RevokeAsync(Guid tenantId, Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// The signer behind an access key id, or null when there is no live credential by that name.
    ///
    /// <para>Null for unknown and for revoked alike. A gateway that answered those differently would
    /// let somebody confirm an access key id, which is the half of the pair that travels in the
    /// clear on every request.</para>
    /// </summary>
    Task<S3Signer?> ResolveAsync(string accessKeyId, CancellationToken cancellationToken);
}
