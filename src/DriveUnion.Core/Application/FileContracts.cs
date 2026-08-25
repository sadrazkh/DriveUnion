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
    /// <param name="nameQuery">
    /// What the reader typed into the shell's search box, or null for the whole list.
    ///
    /// <para>A parameter rather than a separate <c>SearchAsync</c>, because a search is this list
    /// with a <c>WHERE</c> on it: one method means the tenant predicate, the link count and the
    /// ordering cannot drift between the list somebody browses and the list somebody searches.</para>
    ///
    /// <para>Required rather than defaulted. Every caller states whether it is searching, so a
    /// screen that grows a search box and forgets to pass it does not silently list everything —
    /// the same reasoning as <c>tenantId</c> above.</para>
    /// </param>
    Task<IReadOnlyList<FileListItem>> ListAsync(
        Guid tenantId,
        string? nameQuery,
        CancellationToken cancellationToken);

    Task<FileDetail?> GetAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Soft delete. Returns false when the file is not this tenant's, or is already gone.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
