using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>
/// The header a browser sends when it uploads something it encrypted, and gets back when it opens it.
///
/// <para>Carried verbatim in both directions. The server stores these fields and hands them back;
/// nothing on any path that serves bytes reads them, and nothing should start — see
/// <see cref="Storage.FileEncryption"/> for why that is the property rather than an omission.</para>
/// </summary>
public sealed record EncryptionHeader(
    int Scheme,
    int SegmentSize,
    string NoncePrefix,
    long PlaintextLength,
    string KdfSalt,
    int KdfIterations,
    string WrappedKey)
{
    /// <summary>
    /// Whether this is shaped like a header at all.
    ///
    /// <para>Not a check that it is <i>correct</i> — the server cannot know that and could not act on
    /// it if it did. It is a check that the columns will hold it and that the numbers are not
    /// nonsense, so a malformed upload is refused at the door rather than stored and discovered by
    /// whoever tries to open the file six months later.</para>
    /// </summary>
    public bool IsWellFormed =>
        Scheme > 0
        && SegmentSize is > 0 and <= 64 * 1024 * 1024
        && PlaintextLength >= 0
        && KdfIterations is >= 100_000 and <= 10_000_000
        && Fits(NoncePrefix)
        && Fits(KdfSalt)
        && Fits(WrappedKey);

    private static bool Fits(string? value) =>
        value is { Length: > 0 } and { Length: <= Storage.FileEncryption.MaxFieldLength };
}

/// <summary>
/// What a link-fetch decides about its lock before it knows what it is fetching.
///
/// <para>Four fields and not seven, because the other three are not knowable yet: the plaintext
/// length is whatever the source turns out to send, and the scheme and segment size are constants
/// the finished header takes from <see cref="Storage.Du1"/>. The server fills those in when the
/// fetch completes and it knows the length.</para>
///
/// <para><b>These are produced in the browser, and that is the point of them existing.</b> The
/// server used to take the customer's passphrase and derive from it here — defensible, since it is
/// about to hold the plaintext anyway, but it does not stay defensible: people use one secret for
/// everything, so a server that has seen it once could open every file that customer ever locked in
/// their own browser. Now the browser derives, and what arrives is a wrapping of a key to this one
/// file. The customer still chooses the secret; the server just never learns it.</para>
/// </summary>
public sealed record FetchCustody(
    string NoncePrefix,
    string KdfSalt,
    int KdfIterations,
    string WrappedKey)
{
    /// <summary>
    /// Whether this is shaped like custody at all.
    ///
    /// <para>Not a check that it is correct — the server cannot know that and could not act on it if
    /// it did. It is a check that the columns will hold it and the iteration count is not nonsense,
    /// so a malformed request is refused at the door rather than stored and discovered by whoever
    /// tries to open the file.</para>
    /// </summary>
    public bool IsWellFormed =>
        KdfIterations is >= 100_000 and <= 10_000_000
        && Fits(NoncePrefix)
        && Fits(KdfSalt)
        && Fits(WrappedKey);

    private static bool Fits(string? value) =>
        value is { Length: > 0 } and { Length: <= Storage.FileEncryption.MaxFieldLength };
}

/// <summary>
/// One file's content key, wrapped again under a secret made for one link.
///
/// <para>Three fields and not seven: the scheme, the segment size, the nonce prefix and the plaintext
/// length describe the ciphertext and are the same for every link to it. Only custody is duplicated —
/// see <see cref="Sharing.ShareLinkKey"/> for what that buys.</para>
/// </summary>
public sealed record LinkKeyMaterial(string KdfSalt, int KdfIterations, string WrappedKey)
{
    /// <summary>
    /// Whether this is shaped like a re-wrap.
    ///
    /// <para>The same judgement <see cref="EncryptionHeader.IsWellFormed"/> makes and for the same
    /// reason: the server cannot tell whether these bytes unwrap to anything, but it can tell that a
    /// field will not fit its column or that an iteration count is a number nobody would choose. A
    /// malformed one stored is a link that looks fine and opens nothing.</para>
    /// </summary>
    public bool IsWellFormed =>
        KdfIterations is >= 100_000 and <= 10_000_000
        && Fits(KdfSalt)
        && Fits(WrappedKey);

    private static bool Fits(string? value) =>
        value is { Length: > 0 } and { Length: <= Storage.FileEncryption.MaxFieldLength };
}

/// <summary>
/// Reading and writing what a file needs to be opened.
///
/// <para><c>tenantId</c> is explicit on every call, like everywhere else in this product.</para>
/// </summary>
public interface IFileEncryption
{
    /// <summary>The header for one file, or null when the file is not encrypted.</summary>
    Task<EncryptionHeader?> ForFileAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Which side sealed one file, or null when it is not encrypted at all.</summary>
    Task<SealedBy?> SealedByAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of these files are encrypted, and how long each one really is.
    ///
    /// <para>Two columns and not the header: a listing draws a padlock and a size, and sending every
    /// wrapped key to a screen that needed neither would be handing out material for no reason.
    /// Membership is the padlock — a file with no row here is not encrypted.</para>
    ///
    /// <para>The length is here rather than left to <c>StoredFile.SizeBytes</c> because those are two
    /// different numbers for an encrypted file, and the one beside a customer's file name has to be
    /// the file they will get back. The stored figure is what the quota is spent on and is bigger by
    /// one tag per segment; on a small file the difference is one somebody can see.</para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> PlaintextLengthsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);
}
