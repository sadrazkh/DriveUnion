namespace DriveUnion.Core.Storage;

/// <summary>
/// Everything needed to open an encrypted file, none of which opens it.
///
/// <para><b>What this row is.</b> The browser encrypted the file before a byte left the machine and
/// sent us ciphertext. These are the parameters it used, plus the content key <i>wrapped</i> with a
/// key derived from what the customer typed. Given the whole row and no passphrase there is nothing
/// to attack but PBKDF2 at 600,000 rounds — and given the whole row and the passphrase, the file
/// opens. That is the trade the customer made knowingly, and losing the passphrase loses the file.
/// This product has no copy.</para>
///
/// <para><b>Why typed columns rather than a blob.</b> The format is versioned by
/// <see cref="Scheme"/>, and a scheme that grows is a migration with a reason attached — which is
/// what a schema is for. Two of these the server genuinely uses:
/// <see cref="PlaintextLength"/>, because the ciphertext is longer by one tag per segment and the
/// size a customer is shown must be the real one, and <see cref="Scheme"/>, so a future reader can
/// tell what it is looking at without parsing anything.</para>
///
/// <para><b>The server never interprets the rest and must not start.</b> Nothing here is read on any
/// path that serves bytes; it is handed to the browser with the file's details and used there. A
/// server that began deriving keys would be a server that could, which is the whole property this
/// exists to avoid.</para>
/// </summary>
/// <summary>
/// Where a file was encrypted, which is the whole of how strong «encrypted» is for it.
///
/// <para>The two are the same format and open the same way. They are not the same promise, and the
/// product says which is which everywhere it draws a padlock — because the difference is invisible
/// from the outside and is the only thing that matters if the operator is who you are worried
/// about.</para>
/// </summary>
public enum SealedBy
{
    /// <summary>
    /// The browser, before a byte left the machine. The operator never had the plaintext and cannot
    /// have it: not by policy, but because the material never reached this side.
    /// </summary>
    Client = 0,

    /// <summary>
    /// This server, on a file it fetched from a URL on the customer's behalf.
    ///
    /// <para>It defends against everything downstream — Google, a stolen database, anyone who
    /// reaches the bytes at rest — and it does not defend against the operator, because the pull
    /// went through this process and this process held both the plaintext and the secret for the
    /// length of it. A file fetched from a link cannot be encrypted anywhere else; the honest thing
    /// is to do it and say what it is.</para>
    /// </summary>
    Server = 1,
}

public sealed class FileEncryption
{
    /// <summary>The file. Also the key — one encryption row per file, or none.</summary>
    public Guid StoredFileId { get; set; }

    /// <summary>
    /// Which side sealed it. See <see cref="Storage.SealedBy"/> — the two are different promises and
    /// the screen has to be able to tell them apart.
    /// </summary>
    public SealedBy SealedBy { get; set; }

    /// <summary>
    /// Carried here as well as on the file, so that «which of my files are encrypted» is one query
    /// against one table and not a join. The same reasoning <c>FileTag</c> gives for its own copy.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>Which version of the format wrote this. See <c>Scripts/crypto/format.ts</c>.</summary>
    public int Scheme { get; set; }

    /// <summary>Plaintext bytes per segment, stored rather than assumed — see the format's own note.</summary>
    public int SegmentSize { get; set; }

    /// <summary>Base64. The per-file half of every segment's nonce; the other half is the index.</summary>
    public required string NoncePrefix { get; set; }

    /// <summary>
    /// The real size.
    ///
    /// <para><c>StoredFile.SizeBytes</c> is what Drive holds, which is this plus sixteen bytes per
    /// segment. Both are true and they answer different questions: the quota is spent on the
    /// ciphertext, and the number beside the file's name is this.</para>
    /// </summary>
    public long PlaintextLength { get; set; }

    /// <summary>Base64. Salts the derivation, so one cracked passphrase is one file and not all of them.</summary>
    public required string KdfSalt { get; set; }

    public int KdfIterations { get; set; }

    /// <summary>Base64: a twelve-byte nonce followed by the content key sealed under the derived key.</summary>
    public required string WrappedKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The longest a stored base64 field may be.
    ///
    /// <para>The largest of them is the wrapped key at twelve plus thirty-two plus sixteen bytes, so
    /// eighty characters of base64 is already generous. A cap at all because these come from a
    /// browser: a column with no limit is a column somebody puts a megabyte in.</para>
    /// </summary>
    public const int MaxFieldLength = 256;
}
