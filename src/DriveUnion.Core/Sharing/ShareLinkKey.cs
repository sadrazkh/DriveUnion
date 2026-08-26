namespace DriveUnion.Core.Sharing;

/// <summary>
/// One file's content key, wrapped again for one link — so that sharing a locked file does not mean
/// handing over the secret that opens everything else.
///
/// <para><b>The problem this exists for.</b> A file's own <c>FileEncryption</c> row holds its content
/// key wrapped under a key derived from what the owner typed, and a batch of files uploaded together
/// shares that derivation. Before this, giving somebody a locked file meant giving them that
/// passphrase — which opens every file it was used for, for ever, with no way to take it back. The
/// only thing that was actually shareable was the whole custody.</para>
///
/// <para><b>What changes.</b> The owner opens the file once in their own browser, and the browser
/// re-wraps that one content key under a fresh secret generated for this link. Two wrapped copies of
/// the same key now exist, opened by two different secrets: the owner's, and one the recipient can be
/// given. Revoking the link takes the second one away — the row goes with it — and the file is
/// untouched.</para>
///
/// <para><b>What is deliberately not here.</b> The scheme, the segment size, the nonce prefix and the
/// plaintext length stay on the file, because they describe the ciphertext rather than who may open
/// it, and they are identical for every link to it. Only the three fields that constitute custody are
/// duplicated. Nothing here is secret in the sense that matters: given the row and no secret there is
/// PBKDF2 at 600,000 rounds and nothing else, which is the same trade the file's own row makes.</para>
///
/// <para><b>Optional, and its absence means something.</b> A link without one of these serves the
/// file's own wrapped key, which is what shipped with the format and is still right when the owner is
/// sending a file to themselves. The panel says which of the two a link is.</para>
/// </summary>
public sealed class ShareLinkKey
{
    /// <summary>The link. Also the key — one of these per link, or none.</summary>
    public Guid ShareLinkId { get; set; }

    /// <summary>
    /// Carried here as well as on the link, for the reason <c>FileEncryption</c> gives for its own
    /// copy: the wrapped key is the only thing standing between a stolen database and a passphrase,
    /// so reading one is scoped by workspace and not only by an id somebody could guess.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>Base64. Fresh per link, so two links to one file derive two different wrappers.</summary>
    public required string KdfSalt { get; set; }

    public int KdfIterations { get; set; }

    /// <summary>Base64: a twelve-byte nonce followed by the content key sealed under the derived key.</summary>
    public required string WrappedKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
