namespace DriveUnion.Core.Storage;

/// <summary>
/// A folder in the tree a customer sees.
///
/// <para><b>Not a Google Drive folder.</b> The panel already has one of those — see
/// <c>IDriveFolders</c> and <c>StoredFile.DriveFolderId</c> — which is where a customer's bytes
/// physically land, one folder per user under one folder per workspace. This is the other thing
/// entirely: the tree the customer builds and names, held here and nowhere else.</para>
///
/// <para><b>Why the tree is not mirrored into Drive.</b> Every rename and every move would become a
/// Drive call, against a pool with a hard 12,000-queries-per-60-seconds ceiling shared by every
/// workspace, and each one that half-succeeded would leave two trees disagreeing about where a file
/// is — a disagreement the customer sees and cannot fix. A folder is a thing the customer arranges;
/// where the operator's Drive keeps the bytes is not their business and never was.</para>
/// </summary>
public sealed class Folder
{
    public Guid Id { get; set; }

    /// <summary>
    /// The workspace. Carried in the WHERE clause of every query in <c>FolderTree</c>, for the same
    /// reason it is carried everywhere else: there is no global query filter in this model.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Who made it. Required, unlike <c>StoredFile.OwnerUserId</c> — files predate owner tracking
    /// and some rows have none, but no folder can exist without somebody having pressed a button.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Null is the root of the workspace, which is a place rather than a row.</summary>
    public Guid? ParentFolderId { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How deep the tree may go, counting the root as zero.
    ///
    /// <para>A cycle is refused by <c>FolderTree</c> walking the ancestors of a move's destination,
    /// so nothing here can loop. Depth is the other end: unbounded nesting makes the breadcrumb
    /// longer than the screen and the ancestor walk unbounded, and sixteen levels is past what any
    /// human arrangement of files reaches.</para>
    /// </summary>
    public const int MaxDepth = 16;

    /// <summary>The longest name a folder may carry, matching <c>StoredFile.Name</c>.</summary>
    public const int MaxNameLength = 512;
}
