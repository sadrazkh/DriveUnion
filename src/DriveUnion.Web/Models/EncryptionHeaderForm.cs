using DriveUnion.Core.Application;

namespace DriveUnion.Web.Models;

/// <summary>
/// An encryption header as it arrives from a form post.
///
/// <para>A separate type from <see cref="EncryptionHeader"/> rather than binding straight onto it,
/// because that record is a contract — it is what the database stores and what the download page
/// hands back — and a model binder writing directly into it would make every field of it something a
/// caller can shape. Here the caller shapes this, and <see cref="ToHeader"/> is the one place the
/// two meet.</para>
///
/// <para>Nothing here is validated beyond binding. <c>EncryptionHeader.IsWellFormed</c> is the check
/// and it is applied by the service, so there is one answer to "is this a header" rather than two
/// that can disagree.</para>
/// </summary>
public sealed class EncryptionHeaderForm
{
    public int Scheme { get; set; }

    public int SegmentSize { get; set; }

    public string NoncePrefix { get; set; } = "";

    /// <summary>
    /// What the browser thinks the file is.
    ///
    /// <para>Bound and then ignored: the catalogue already knows the length, the browser is
    /// describing a file it has never read, and a header whose length disagrees with its own
    /// ciphertext cannot open it. Kept on the form so the shape matches what the seal produced,
    /// rather than being a field the script has to remember to leave out.</para>
    /// </summary>
    public long PlaintextLength { get; set; }

    public string KdfSalt { get; set; } = "";

    public int KdfIterations { get; set; }

    public string WrappedKey { get; set; } = "";

    public EncryptionHeader ToHeader() => new(
        Scheme,
        SegmentSize,
        NoncePrefix,
        PlaintextLength,
        KdfSalt,
        KdfIterations,
        WrappedKey);
}

/// <summary>
/// A link-fetch's custody as it arrives from a form post.
///
/// <para>Four fields rather than a whole header, because a fetch does not know what it is fetching:
/// the plaintext length is whatever the source turns out to send, and the scheme and segment size
/// are constants the finished header takes from <c>Du1</c>. See <see cref="FetchCustody"/>.</para>
///
/// <para>A separate type from that record for the reason <see cref="EncryptionHeaderForm"/> is
/// separate from <c>EncryptionHeader</c>: a model binder writing straight into a contract makes
/// every field of the contract something a caller can shape.</para>
/// </summary>
public sealed class FetchCustodyForm
{
    public string NoncePrefix { get; set; } = "";

    public string KdfSalt { get; set; } = "";

    public int KdfIterations { get; set; }

    public string WrappedKey { get; set; } = "";

    /// <summary>
    /// Null when nothing was sent, which is «store it as it comes» rather than a malformed request.
    ///
    /// <para>A form post binds an absent object to one with empty strings rather than to null, so
    /// «did the caller ask for a lock at all» has to be asked here. Getting it wrong in the other
    /// direction would refuse every unencrypted fetch.</para>
    /// </summary>
    public FetchCustody? ToCustody() =>
        WrappedKey.Length == 0 && KdfSalt.Length == 0 && NoncePrefix.Length == 0
            ? null
            : new FetchCustody(NoncePrefix, KdfSalt, KdfIterations, WrappedKey);
}
