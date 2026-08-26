namespace DriveUnion.Core.Application;

/// <summary>
/// Where a file's bytes physically are.
///
/// <para><b>This record never leaves the server</b>, and there is no path from a JSON response to
/// any of it. <c>IFileCatalog</c> is what the panel and the API read, and it names no account on
/// purpose — the customer must never learn that a pool exists, let alone which of the operator's
/// Google accounts holds their file. The same discipline the Telegram drainer's own reference
/// carries, for the same reason.</para>
/// </summary>
/// <param name="IsEncrypted">
/// Whether what is stored is ciphertext.
///
/// <para>Carried here because every caller of this record is about to hand those bytes to somebody,
/// and there are three — the S3 gateway, the API's content route, and the Telegram drainer — that
/// cannot decrypt and must not pretend. A client given an encrypted object with no warning gets a
/// file of the right length, the right name and no readable content, which is the failure mode this
/// flag exists to make impossible.</para>
/// </param>
public sealed record StoredFileBytes(
    Guid GoogleAccountId,
    string DriveFileId,
    string MimeType,
    long SizeBytes,
    bool IsEncrypted);

/// <summary>
/// The one lookup that turns a customer's file id into somewhere to stream from.
///
/// <para>Separate from <see cref="IFileCatalog"/> rather than a method on it, so the type that must
/// not appear in a response cannot be reached from the interface every response is built out of. A
/// controller has to ask for this by name, which is a line in a constructor that a reader will
/// notice.</para>
///
/// <para><c>tenantId</c> is explicit and is applied in the WHERE: another workspace's file id is not
/// found rather than found and refused.</para>
/// </summary>
public interface IStoredFileBytes
{
    Task<StoredFileBytes?> ResolveAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
