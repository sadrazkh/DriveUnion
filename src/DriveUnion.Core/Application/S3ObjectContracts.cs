namespace DriveUnion.Core.Application;

/// <param name="Key">The object key: the folder path and the file name, joined with <c>/</c>.</param>
public sealed record S3Object(string Key, long SizeBytes, DateTimeOffset ModifiedAt, string ETag);

/// <param name="Objects">Matching objects, by key.</param>
/// <param name="CommonPrefixes">
/// The folder names one level down, each with the delimiter on the end — what an S3 client draws as
/// a directory. Empty when the caller asked for no delimiter, because then there are no directories,
/// only keys.
/// </param>
/// <param name="IsTruncated">Whether the listing stopped at <c>max-keys</c> rather than at the end.</param>
/// <param name="NextToken">Where a continued listing resumes, or null when there is nothing after.</param>
public sealed record S3Listing(
    IReadOnlyList<S3Object> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextToken);

/// <summary>Where an object key points, once the tree has been walked.</summary>
public sealed record S3Located(Guid FileId, Guid? FolderId, string Name, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>
/// The S3 gateway's view of a workspace: keys in, files and folders out.
///
/// <para><b>The mapping.</b> A workspace is one bucket. An object key is the folder path and the file
/// name joined with <c>/</c> — <c>Reports/2026/Q3.pdf</c> is the file <c>Q3.pdf</c> in the folder
/// <c>2026</c> in the folder <c>Reports</c>. S3 has no folders at all, only keys that happen to
/// contain slashes; this product has a real tree, and the two are reconciled here and nowhere
/// else.</para>
///
/// <para><b>What that costs, stated.</b> S3 keys are unique and this product's file names are not:
/// nothing stops two files called <c>Q3.pdf</c> sitting in one folder, because a panel that refused
/// the second would be a panel that argues with a customer about names. So a key resolves to the
/// most recently modified match, and a PUT to an existing key replaces rather than adds. That is
/// S3's semantics honoured over a store that does not natively have them, which is the whole job of
/// a gateway.</para>
/// </summary>
public interface IS3Objects
{
    Task<S3Listing> ListAsync(
        Guid tenantId,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        int maxKeys,
        CancellationToken cancellationToken);

    /// <summary>The file a key names, or null.</summary>
    Task<S3Located?> LocateAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    /// <summary>
    /// The folder a key's path names, creating the chain if it is not there.
    ///
    /// <para>Null for a key with no slash in it, which is the bucket's root. Returns null <i>and</i>
    /// sets <paramref name="refused"/> when a folder in the path cannot be made — a name past the
    /// depth limit, most likely — so a PUT answers rather than half-creating a tree.</para>
    /// </summary>
    Task<(Guid? FolderId, FolderOutcome Refused)> EnsurePathAsync(
        Guid tenantId,
        Guid ownerUserId,
        string key,
        CancellationToken cancellationToken);
}
