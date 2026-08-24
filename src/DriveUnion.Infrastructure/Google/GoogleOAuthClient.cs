namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// One Google OAuth client the operator pasted into the panel, as a row.
///
/// <para><b>Why a row, and not the file this used to be.</b> The first version of this wrote
/// <c>App_Data/google-oauth.json</c> inside the container. A redeploy destroyed it. The accounts'
/// refresh tokens survived — they are rows, encrypted with a Data Protection key ring that is itself
/// a table — but a refresh needs the client id and secret to present alongside the token, and those
/// had just been deleted. So the entire pool went dark on a deploy, and the only thing anybody saw
/// was a customer being told storage was unavailable. The database is the one store in this product
/// that is known to survive a deploy, and this is it.</para>
///
/// <para>It lives in <c>Infrastructure.Google</c> rather than in <c>Core.Storage</c> because it is
/// not a domain object. It is a credential for one external API, and nothing in Core has any
/// business knowing that Google issues them.</para>
/// </summary>
public sealed class GoogleOAuthClient
{
    public Guid Id { get; set; }

    /// <summary>
    /// Short operator-facing handle: <c>C1</c>, <c>C2</c>. Allocated the same way account labels are
    /// and for the same reason — a client id is seventy-odd characters of noise, and the account
    /// cards have to name the client that connected them in something a human can read back.
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// Google's own client id. Not a secret: it travels in the authorization URL, in plain sight of
    /// the operator's browser and of Google.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// Encrypted at rest with the same <see cref="Core.Abstractions.ITokenProtector"/> that protects
    /// the refresh tokens. Null when the operator has saved a client id without a secret, which is a
    /// client that cannot exchange or refresh anything and which the screen says so about.
    /// </summary>
    public string? ClientSecretProtected { get; set; }

    public required string RedirectUri { get; set; }

    /// <summary>
    /// The stored client a new consent flow runs with, when configuration is not supplying one.
    ///
    /// <para>A flag rather than "the newest row" or "the first row", because adding a second client
    /// must not silently move which one the next connection binds an account to — that is the
    /// mistake this whole change exists to make impossible. Promoting one is a deliberate press on
    /// the accounts screen.</para>
    ///
    /// <para>At most one row carries it; <c>GoogleOAuthClientStore</c> clears the others in the same
    /// transaction that sets it. If two ever did, the resolver takes the oldest, so the answer is at
    /// least stable rather than a property of row order.</para>
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
