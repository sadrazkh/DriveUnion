namespace DriveUnion.Core.Application;

/// <summary>One folder as a row on the screen, with enough to say whether it is worth opening.</summary>
public sealed record FolderNode(Guid Id, string Name, int FileCount, int SubfolderCount);

/// <summary>One step of the breadcrumb, root first, the folder itself last.</summary>
public sealed record FolderCrumb(Guid Id, string Name);

/// <summary>
/// A folder as an option in a «move to…» list: the full path, so two folders called «۲۰۲۶» in
/// different places are told apart, and the depth so the list can be indented.
/// </summary>
public sealed record FolderChoice(Guid Id, string Path, int Depth);

/// <summary>
/// What happened, in the words the screen has to answer with.
///
/// <para>An outcome rather than an exception, and rather than a bool: «that name is taken», «that
/// folder still has things in it» and «that move would put a folder inside itself» are three
/// different sentences a customer needs, and a bool collapses them into «no».</para>
/// </summary>
public enum FolderOutcome
{
    Done,

    /// <summary>Not this workspace's, or already gone.</summary>
    NotFound,

    /// <summary>A folder of that name is already in the same place.</summary>
    NameTaken,

    /// <summary>Nothing, or only spaces.</summary>
    NameEmpty,

    /// <summary>Still holds files or folders. See <c>IFolderTree.DeleteAsync</c> for why that refuses.</summary>
    NotEmpty,

    /// <summary>The destination is the folder itself or one of its descendants.</summary>
    WouldLoop,

    /// <summary>Past <see cref="Storage.Folder.MaxDepth"/>.</summary>
    TooDeep,
}

/// <param name="Contains">
/// For <see cref="FolderOutcome.NotEmpty"/>, how many things are in the way — so the refusal can
/// say «۱۲ فایل» rather than «not empty», which is the difference between a sentence somebody can
/// act on and one they have to go and investigate.
/// </param>
public sealed record FolderResult(FolderOutcome Outcome, Guid? FolderId = null, int Contains = 0)
{
    public bool Succeeded => Outcome == FolderOutcome.Done;
}

/// <summary>
/// The customer's own folder tree.
///
/// <para><c>tenantId</c> is explicit on every call, like everywhere else in this product and for the
/// same reason — there is no global query filter, because <c>/d/{slug}</c> is anonymous and a filter
/// fed by the signed-in user resolves it to nobody.</para>
/// </summary>
public interface IFolderTree
{
    /// <summary>The folders directly inside one folder, or inside the root when <paramref name="parentId"/> is null.</summary>
    Task<IReadOnlyList<FolderNode>> ChildrenAsync(Guid tenantId, Guid? parentId, CancellationToken cancellationToken);

    /// <summary>Root first, the folder itself last. Empty when the folder is not this workspace's.</summary>
    Task<IReadOnlyList<FolderCrumb>> PathAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken);

    /// <summary>
    /// Every folder in the workspace as a «move to…» option.
    /// </summary>
    /// <param name="excludingSubtreeOf">
    /// A folder being moved, and everything under it. Offering a folder its own descendants as a
    /// destination is offering the one move that is always refused.
    /// </param>
    Task<IReadOnlyList<FolderChoice>> ChoicesAsync(
        Guid tenantId,
        Guid? excludingSubtreeOf,
        CancellationToken cancellationToken);

    Task<FolderResult> CreateAsync(
        Guid tenantId,
        Guid ownerUserId,
        Guid? parentId,
        string name,
        CancellationToken cancellationToken);

    Task<FolderResult> RenameAsync(Guid tenantId, Guid folderId, string name, CancellationToken cancellationToken);

    Task<FolderResult> MoveAsync(Guid tenantId, Guid folderId, Guid? newParentId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a folder that has nothing live in it, for good.
    ///
    /// <para><b>Empty only, and a hard delete.</b> An empty folder is a name; there is nothing in it
    /// to keep, so it does not go to the trash and there is no folder to restore. A folder with
    /// files in it is refused with the count, because deleting it would mean a Drive round trip per
    /// descendant inside one form post — a folder of two hundred files is a minute of somebody
    /// watching a spinner, and half of it landing is a tree that disagrees with the pool. Recursive
    /// delete belongs to whatever runs the purge sweeper, not to a button.</para>
    ///
    /// <para>Files already in the trash do not block it, and a folder can be deleted out from under
    /// them: a restore whose folder is gone lands at the root and says so.</para>
    /// </summary>
    Task<FolderResult> DeleteAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken);

    /// <summary>Whether this workspace has that folder — the check every screen makes before drawing one.</summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken);

    /// <summary>Moves one file into a folder, or to the root when <paramref name="folderId"/> is null.</summary>
    Task<FolderResult> MoveFileAsync(
        Guid tenantId,
        Guid fileId,
        Guid? folderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same for a selection, in one statement.
    ///
    /// <para>Unbounded on purpose, unlike anything that deletes: filing costs no Drive call at all —
    /// see <see cref="Storage.Folder"/> — so moving four hundred files is one UPDATE and not four
    /// hundred round trips to Google. <c>Contains</c> on the result is how many actually moved,
    /// which is not how many were asked for: ids that are not this workspace's, or are in the trash,
    /// are simply not matched.</para>
    /// </summary>
    Task<FolderResult> MoveFilesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        Guid? folderId,
        CancellationToken cancellationToken);
}
