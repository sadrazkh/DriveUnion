namespace DriveUnion.Core.Storage;

public enum GoogleAccountStatus
{
    /// <summary>Connected, token refreshable, accepting uploads.</summary>
    Healthy = 0,

    /// <summary>Refresh failed. The operator has to reconnect; uploads must not be routed here.</summary>
    Disconnected = 1,

    /// <summary>Connected but withheld from upload routing by the operator.</summary>
    Paused = 2,
}

/// <summary>
/// One Google Drive account in the operator's pool.
///
/// Deliberately has no tenant. Customers never authenticate with Google and must never learn which
/// account holds their file — that is what keeps OAuth verification and the CASA assessment off the
/// critical path, and it only stays true if nothing here leaks into a tenant-facing response.
/// </summary>
public sealed class GoogleAccount
{
    public Guid Id { get; set; }

    /// <summary>
    /// What the operator reads on the card. Deliberately not the identity — see
    /// <see cref="GoogleUserId"/>.
    /// </summary>
    public required string Email { get; set; }

    /// <summary>
    /// Drive's <c>permissionId</c>: the account's own stable id, and what two rows are compared on.
    ///
    /// The address is not an identity. Gmail treats <c>archive.main@gmail.com</c>,
    /// <c>archive.main+cold@gmail.com</c> and <c>archivemain@gmail.com</c> as one mailbox and
    /// reports back whichever spelling was typed, so keying on it lets one account be connected
    /// twice — two labels, and a pool that believes it has five terabytes it does not have.
    ///
    /// Nullable for the rows written before this column existed. They are matched by address until
    /// their next reconnect fills this in, which is why the unique index is filtered rather than
    /// plain: several unknown identities are not a collision, two identical known ones are.
    /// </summary>
    public string? GoogleUserId { get; set; }

    /// <summary>Short operator-facing handle: <c>A1</c>, <c>A2</c>. Shown on the account cards.</summary>
    public required string Label { get; set; }

    /// <summary>
    /// Google's own client id — the <c>…apps.googleusercontent.com</c> string — of the OAuth client
    /// this account was connected under.
    ///
    /// <para><b>A refresh token belongs to the client that issued it.</b> Presenting it to a
    /// different client is an <c>invalid_grant</c>, which this product turns into "reconnect this
    /// account", so the moment the panel holds more than one client the refresh has to know which
    /// one — and get it from here rather than from whatever happens to be in force. That is the part
    /// of multi-client support that looks like it works until the first hour elapses.</para>
    ///
    /// <para>The client id itself and not a key into the clients table, because it is what Google
    /// binds the grant to: removing a stored client and pasting the same one back leaves these rows
    /// pointing at a client that still works, where a foreign key would leave them pointing at
    /// nothing. It also covers the client a deployment supplies from its environment, which has no
    /// row at all.</para>
    ///
    /// <para>Null on the rows written before this column existed. They are refreshed with the client
    /// in force — which is the client that connected them, because there was only one — and this is
    /// filled in the first time that succeeds, so the fallback retires itself.</para>
    /// </summary>
    public string? OAuthClientId { get; set; }

    /// <summary>Encrypted at rest. Never logged, never returned by an API.</summary>
    public required string RefreshTokenProtected { get; set; }

    /// <summary>Encrypted at rest. Google access tokens live about an hour.</summary>
    public string? AccessTokenProtected { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    /// <summary>Root folder id of <c>DriveUnion/</c> inside this account, created on first upload.</summary>
    public string? RootFolderId { get; set; }

    public long QuotaTotalBytes { get; set; }

    public long QuotaUsedBytes { get; set; }

    public GoogleAccountStatus Status { get; set; }

    /// <summary>
    /// Why this account last stopped working, for the operator.
    ///
    /// <para><see cref="Status"/> says <c>Disconnected</c> and nothing said why. The customer's
    /// sentence — «storage is unavailable» — is correct for them, because Google's own error text can
    /// carry a session URI or the address of an account they must never learn about. But the operator
    /// is the one person who can fix it, and they were being told nothing at all: a pool that died
    /// because a redeploy deleted the OAuth client read exactly like a pool whose consent screen had
    /// expired, and both read like nothing.</para>
    ///
    /// <para>Google's words, kept as they arrived and not translated: this is a diagnostic, and a
    /// paraphrase of an OAuth error is worth less than the string that can be searched for. It is
    /// only ever rendered on the operator-only accounts screen.</para>
    /// </summary>
    public string? LastFailureReason { get; set; }

    /// <summary>
    /// When <see cref="LastFailureReason"/> was recorded. Without it the card cannot tell a failure
    /// from a minute ago from one that was fixed by a reconnection last week.
    /// </summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// True when the cached access token is missing or close enough to expiry that a request started
    /// now could outlive it. The 60-second skew is not politeness — a chunk upload begun at T+3599
    /// fails halfway through with a 401 that looks like a network fault.
    /// </summary>
    public bool NeedsAccessToken(DateTimeOffset now) =>
        AccessTokenProtected is null
        || AccessTokenExpiresAt is null
        || now >= AccessTokenExpiresAt.Value.AddSeconds(-60);
}
