namespace DriveUnion.Core.Application;

public sealed record FileListItem(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    int ActiveLinkCount);

public sealed record FileDetail(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<ShareLinkSummary> Links);

/// <summary>
/// A tenant's own files.
///
/// Every method takes <c>tenantId</c> explicitly, and that is the design rather than an oversight:
/// there is no global query filter in this product because /d/{slug} is anonymous and a filter fed
/// by the signed-in user resolves it to nobody. An explicit argument turns a forgotten scope into a
/// compile error instead of an empty result set.
///
/// Nothing returned here mentions a Google account. The customer must not learn which one holds
/// their file.
/// </summary>
public interface IFileCatalog
{
    Task<IReadOnlyList<FileListItem>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<FileDetail?> GetAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Soft delete. Returns false when the file is not this tenant's, or is already gone.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
