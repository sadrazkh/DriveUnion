namespace DriveUnion.Core.Application;

/// <param name="FolderId">
/// Where the customer filed it, so a search result can say where it was found. Null is the root.
/// The id and not the path: turning one into the other needs the whole tree, and the screen has
/// that already.
/// </param>
public sealed record FileListItem(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    int ActiveLinkCount,
    Guid? FolderId);

public sealed record FileDetail(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<ShareLinkSummary> Links);

/// <summary>
/// Which of a workspace's files a listing wants.
///
/// <para>A record rather than three more parameters, and it is the third time this signature has
/// grown: <c>nameQuery</c>, then <c>folderId</c>, now <c>TagId</c>. Each of those was a sweep of
/// every call site in the product for one more argument nobody at the far end cared about. A filter
/// with defaults is one type to add a field to.</para>
/// </summary>
/// <param name="FolderId">
/// The folder being browsed, or null for the workspace's root. <b>Ignored whenever this filter is
/// workspace-wide</b>, and that is the design rather than an oversight: looking inside the folder
/// somebody happens to be standing in answers «not found» for a file they own and can see the name
/// of, which is the failure the search box already had once. <c>FileListItem.FolderId</c> is what
/// lets the screen say where each hit was found instead.
/// </param>
/// <param name="NameQuery">What the reader typed into the shell's search box, or null.</param>
/// <param name="TagId">A label to filter by, or null. Combines with the query rather than replacing it.</param>
public sealed record FileListFilter(
    Guid? FolderId = null,
    string? NameQuery = null,
    Guid? TagId = null)
{
    /// <summary>The trimmed term, or null — so «?q=» and a box full of spaces are not a search.</summary>
    public string? Term => NameQuery?.Trim() is { Length: > 0 } typed ? typed : null;

    /// <summary>
    /// Whether this reaches past one folder. Searching and filtering by tag both do: neither is a
    /// question about where the reader is standing.
    /// </summary>
    public bool IsWorkspaceWide => Term is not null || TagId is not null;
}

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
        FileListFilter filter,
        CancellationToken cancellationToken);

    Task<FileDetail?> GetAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Soft delete. Returns false when the file is not this tenant's, or is already gone.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
