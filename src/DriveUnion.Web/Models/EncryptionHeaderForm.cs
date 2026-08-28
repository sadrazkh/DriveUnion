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
