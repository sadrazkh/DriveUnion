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
/// Reading and writing what a file needs to be opened.
///
/// <para><c>tenantId</c> is explicit on every call, like everywhere else in this product.</para>
/// </summary>
public interface IFileEncryption
{
    /// <summary>The header for one file, or null when the file is not encrypted.</summary>
    Task<EncryptionHeader?> ForFileAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Which of these files are encrypted.
    ///
    /// <para>A set rather than a header per file: a listing draws a marker and nothing more, and
    /// sending every wrapped key to a screen that only needed a padlock would be handing out
    /// material for no reason.</para>
    /// </summary>
    Task<IReadOnlySet<Guid>> EncryptedAmongAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);
}
