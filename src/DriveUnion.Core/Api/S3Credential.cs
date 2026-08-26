namespace DriveUnion.Core.Api;

/// <summary>
/// An access key pair for the S3 gateway.
///
/// <para><b>Why this is not an <see cref="ApiToken"/>.</b> A bearer key is presented whole, so the
/// server can hash what arrives and compare — and that is why <c>ApiToken</c> keeps a SHA-256 and
/// nothing else. AWS Signature V4 presents no secret at all: the client signs the request with it
/// and sends the signature, and verifying that means <b>recomputing the same HMAC chain</b>, which
/// means holding the secret. There is no version of SigV4 where a one-way hash is enough.</para>
///
/// <para>So this one is encrypted rather than hashed, with the same data-protection key ring that
/// holds the operator's Google refresh tokens — and the screen that mints it says so, in those
/// words. A customer choosing between the two credentials is choosing between «we cannot read this»
/// and «we can, because the protocol requires it», and that is a decision they are entitled to make
/// knowingly rather than one to bury.</para>
///
/// <para>Consequence, stated where it belongs rather than discovered: whoever can read this
/// database <i>and</i> the key ring can sign requests as this customer. The mitigation is the same
/// one the refresh tokens have — the key ring lives in the database under Data Protection, and the
/// two are only useful together — plus a scope and a revoke button.</para>
/// </summary>
public sealed class S3Credential
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Whose folder objects uploaded with it land in, as with an <see cref="ApiToken"/>.</summary>
    public Guid OwnerUserId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The public half, and what arrives in the <c>Credential=</c> part of an Authorization header.
    ///
    /// <para>Shaped like AWS's own — twenty upper-case base32 characters — because clients and
    /// config files treat it as opaque but humans expect it to look like one, and a credential that
    /// looks wrong is one somebody assumes they pasted badly.</para>
    /// </summary>
    public required string AccessKeyId { get; set; }

    /// <summary>The secret, encrypted at rest. See the type's own summary for why this is not a hash.</summary>
    public required string SecretProtected { get; set; }

    public ApiScope Scope { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public const int AccessKeyIdLength = 20;

    /// <summary>Forty base64url characters, which is what AWS's own secrets are the length of.</summary>
    public const int SecretLength = 40;

    public const int MaxNameLength = 80;

    public const int MaxPerTenant = 10;

    /// <summary>
    /// What every access key id starts with.
    ///
    /// <para><c>AKIA</c> is AWS's own and must not be borrowed: a key that looks like an Amazon key
    /// is one somebody will paste into a tool that then talks to Amazon, and one that a leak
    /// scanner will report to the wrong provider. <c>DUIA</c> is the same shape and unmistakably
    /// not theirs.</para>
    /// </summary>
    public const string Marker = "DUIA";

    public bool IsLive => RevokedAt is null;
}
