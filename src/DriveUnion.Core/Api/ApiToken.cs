namespace DriveUnion.Core.Api;

/// <summary>What a token is allowed to do. Two, because a third would be a permission model nobody asked for.</summary>
public enum ApiScope : byte
{
    /// <summary>List, read and download. Changes nothing.</summary>
    Read = 0,

    /// <summary>Everything <see cref="Read"/> does, plus upload, file, delete and link.</summary>
    Write = 1,
}

/// <summary>
/// A key a customer's own program uses instead of a browser session.
///
/// <para><b>Stored as a hash, and the secret is shown once.</b> This is the same discipline a
/// password gets, for a stronger reason: a token is a bearer credential with no second factor and no
/// account behind it to lock. A row that held the secret would turn one read of this table into
/// every customer's files.</para>
///
/// <para><b>SHA-256 and not a slow KDF</b>, which is the one place this deliberately differs from a
/// password. A password is short, chosen by a person and guessable, so hashing it has to be
/// expensive. A token is 32 bytes from a cryptographic RNG — there is nothing to guess, and the
/// expense would land on every API request instead of on a login.</para>
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Who minted it. The uploads it makes land in this person's folder, exactly as their panel
    /// uploads do — a token is somebody acting through a program, not a second kind of member.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>What the customer called it, so a list of four keys is four answers to «which one is this».</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The first characters of the secret, kept in the clear on purpose.
    ///
    /// <para>It is how a request finds its row without a table scan of hashes, and it is what the
    /// panel shows so somebody can tell which of their keys is in which deployment. Long enough to
    /// be unique in practice and far too short to be worth guessing at.</para>
    /// </summary>
    public required string Prefix { get; set; }

    /// <summary>Base64 of the SHA-256 of the whole presented token, prefix included.</summary>
    public required string SecretHash { get; set; }

    public ApiScope Scope { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When it was last accepted, to the hour.
    ///
    /// <para>Rounded because it is written on the authentication path: to the second it would be an
    /// UPDATE on every request of every key, for a figure whose only reader is a human deciding
    /// whether a key is still in use.</para>
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Null never expires. A key with no end is the one somebody forgets they issued.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set rather than deleted, so «this key was revoked» outlives the key.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The most a workspace may hold at once, live.</summary>
    public const int MaxPerTenant = 20;

    public const int MaxNameLength = 80;

    /// <summary>
    /// The characters of the secret kept in the clear.
    ///
    /// <para>Eight base64url characters is 48 bits, so a workspace's twenty keys colliding is not a
    /// thing that happens — and the lookup does not rely on it being unique anyway: the prefix
    /// narrows, and the hash decides.</para>
    /// </summary>
    public const int PrefixLength = 8;

    /// <summary>
    /// What every token starts with.
    ///
    /// <para>A fixed marker so a leaked key is recognisable as one — by its owner reading a log, and
    /// by the secret scanners that watch public repositories for exactly this shape. A key that
    /// looks like any other random string is one nobody notices in a commit.</para>
    /// </summary>
    public const string Marker = "du_";

    public bool IsLive(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || now < ExpiresAt);
}
