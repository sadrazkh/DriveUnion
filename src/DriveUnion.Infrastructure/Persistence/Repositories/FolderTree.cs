using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The customer's folder tree, read and written whole-workspace-at-a-time.
///
/// <para><b>Why the whole workspace.</b> A breadcrumb is an ancestor walk, a «move to…» list is
/// every folder with its path, and a cycle check is an ancestor walk from the destination. Done in
/// SQL each of those is a query per level — a recursive CTE would do it in one, and would be one
/// piece of provider-specific SQL for Postgres and another for the SQLite the tests run on. A
/// workspace's folders are tens of rows; they are read once into memory and walked there, and the
/// arithmetic stops being a database question.</para>
///
/// <para>Every query carries <c>tenantId</c>, because this model has no global query filter — see
/// <see cref="DriveUnionDbContext.OnModelCreating"/>. Once the rows are in memory the tenant
/// predicate has already been applied, so nothing walked here can reach another workspace.</para>
/// </summary>
public sealed class FolderTree(DriveUnionDbContext db, TimeProvider clock) : IFolderTree
{
    public async Task<IReadOnlyList<FolderNode>> ChildrenAsync(
        Guid tenantId,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        var children = await db.Folders
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ParentFolderId == parentId)
            .Select(f => new
            {
                f.Id,
                f.Name,

                // Live files only. A folder whose contents are all in the trash reads as empty
                // because it is: the customer put those somewhere else, and a count that included
                // them would be a number they cannot see the parts of.
                FileCount = db.StoredFiles.Count(s =>
                    s.TenantId == tenantId && s.FolderId == f.Id && s.DeletedAt == null),
                SubfolderCount = db.Folders.Count(s => s.TenantId == tenantId && s.ParentFolderId == f.Id),
            })
            .ToListAsync(cancellationToken);

        // Ordered here rather than in SQL, like FileCatalog.ListAsync: collation decides what
        // ORDER BY means for a Persian folder name, and the two providers do not agree. In memory
        // it is one comparison this application chooses, and a workspace's folders are tens of rows.
        return
        [
            .. children
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new FolderNode(f.Id, f.Name, f.FileCount, f.SubfolderCount)),
        ];
    }

    public async Task<IReadOnlyList<FolderCrumb>> PathAsync(
        Guid tenantId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var all = await FlatAsync(tenantId, cancellationToken);
        if (!all.TryGetValue(folderId, out var folder)) return [];

        var crumbs = new List<FolderCrumb>();

        // Bounded by MaxDepth rather than by reaching the root, so a cycle that somehow got written
        // — a bad migration, a hand-edited row — renders a truncated breadcrumb instead of hanging
        // the request. Nothing in this class can create one; that is exactly why the guard is here.
        for (var i = 0; i <= Folder.MaxDepth && folder is not null; i++)
        {
            crumbs.Add(new FolderCrumb(folder.Id, folder.Name));
            folder = folder.ParentFolderId is { } parent && all.TryGetValue(parent, out var next) ? next : null;
        }

        crumbs.Reverse();
        return crumbs;
    }

    public async Task<IReadOnlyList<FolderChoice>> ChoicesAsync(
        Guid tenantId,
        Guid? excludingSubtreeOf,
        CancellationToken cancellationToken)
    {
        var all = await FlatAsync(tenantId, cancellationToken);
        var byParent = all.Values
            .GroupBy(f => f.ParentFolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList());

        var choices = new List<FolderChoice>();

        Walk(Guid.Empty, string.Empty, 0);

        return choices;

        void Walk(Guid parent, string prefix, int depth)
        {
            if (depth > Folder.MaxDepth || !byParent.TryGetValue(parent, out var children)) return;

            foreach (var child in children)
            {
                // The folder being moved and everything under it, left out in one branch: descending
                // no further is what removes the subtree, and skipping only the folder itself would
                // still offer its children as destinations.
                if (child.Id == excludingSubtreeOf) continue;

                var path = prefix.Length == 0 ? child.Name : $"{prefix} / {child.Name}";

                choices.Add(new FolderChoice(child.Id, path, depth));
                Walk(child.Id, path, depth + 1);
            }
        }
    }

    public async Task<FolderResult> CreateAsync(
        Guid tenantId,
        Guid ownerUserId,
        Guid? parentId,
        string name,
        CancellationToken cancellationToken)
    {
        if (Clean(name) is not { } cleaned) return new FolderResult(FolderOutcome.NameEmpty);

        var all = await FlatAsync(tenantId, cancellationToken);

        if (parentId is { } parent)
        {
            if (!all.ContainsKey(parent)) return new FolderResult(FolderOutcome.NotFound);
            if (DepthOf(all, parent) + 1 > Folder.MaxDepth) return new FolderResult(FolderOutcome.TooDeep);
        }

        if (Taken(all, parentId, cleaned, exceptFolderId: null)) return new FolderResult(FolderOutcome.NameTaken);

        var folder = new Folder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            ParentFolderId = parentId,
            Name = cleaned,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Folders.Add(folder);
        await db.SaveChangesAsync(cancellationToken);

        return new FolderResult(FolderOutcome.Done, folder.Id);
    }

    public async Task<FolderResult> RenameAsync(
        Guid tenantId,
        Guid folderId,
        string name,
        CancellationToken cancellationToken)
    {
        if (Clean(name) is not { } cleaned) return new FolderResult(FolderOutcome.NameEmpty);

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == folderId && f.TenantId == tenantId, cancellationToken);

        if (folder is null) return new FolderResult(FolderOutcome.NotFound);

        var all = await FlatAsync(tenantId, cancellationToken);

        if (Taken(all, folder.ParentFolderId, cleaned, exceptFolderId: folderId))
        {
            return new FolderResult(FolderOutcome.NameTaken);
        }

        folder.Name = cleaned;
        await db.SaveChangesAsync(cancellationToken);

        return new FolderResult(FolderOutcome.Done, folder.Id);
    }

    public async Task<FolderResult> MoveAsync(
        Guid tenantId,
        Guid folderId,
        Guid? newParentId,
        CancellationToken cancellationToken)
    {
        if (folderId == newParentId) return new FolderResult(FolderOutcome.WouldLoop);

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == folderId && f.TenantId == tenantId, cancellationToken);

        if (folder is null) return new FolderResult(FolderOutcome.NotFound);

        var all = await FlatAsync(tenantId, cancellationToken);

        if (newParentId is { } parent)
        {
            if (!all.ContainsKey(parent)) return new FolderResult(FolderOutcome.NotFound);

            // Walking up from the destination and not down from the folder: a subtree can be wide,
            // and the ancestor chain is at most MaxDepth long whatever shape the tree has.
            if (IsDescendantOfOrSelf(all, parent, folderId)) return new FolderResult(FolderOutcome.WouldLoop);

            if (DepthOf(all, parent) + 1 + HeightOf(all, folderId) > Folder.MaxDepth)
            {
                return new FolderResult(FolderOutcome.TooDeep);
            }
        }

        if (Taken(all, newParentId, folder.Name, exceptFolderId: folderId))
        {
            return new FolderResult(FolderOutcome.NameTaken);
        }

        folder.ParentFolderId = newParentId;
        await db.SaveChangesAsync(cancellationToken);

        return new FolderResult(FolderOutcome.Done, folder.Id);
    }

    public async Task<FolderResult> DeleteAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == folderId && f.TenantId == tenantId, cancellationToken);

        if (folder is null) return new FolderResult(FolderOutcome.NotFound);

        var files = await db.StoredFiles
            .CountAsync(f => f.TenantId == tenantId && f.FolderId == folderId && f.DeletedAt == null, cancellationToken);

        var subfolders = await db.Folders
            .CountAsync(f => f.TenantId == tenantId && f.ParentFolderId == folderId, cancellationToken);

        if (files + subfolders > 0)
        {
            return new FolderResult(FolderOutcome.NotEmpty, folderId, files + subfolders);
        }

        // Files in the trash that name this folder are left pointing at a row that is about to go.
        // That is the case FilesController's restore handles by landing them at the root and saying
        // so — see StoredFile.FolderId for why there is no foreign key to make this impossible.
        db.Folders.Remove(folder);
        await db.SaveChangesAsync(cancellationToken);

        return new FolderResult(FolderOutcome.Done, folder.ParentFolderId);
    }

    public Task<bool> ExistsAsync(Guid tenantId, Guid folderId, CancellationToken cancellationToken) =>
        db.Folders.AnyAsync(f => f.Id == folderId && f.TenantId == tenantId, cancellationToken);

    public async Task<FolderResult> MoveFileAsync(
        Guid tenantId,
        Guid fileId,
        Guid? folderId,
        CancellationToken cancellationToken)
    {
        if (folderId is { } destination
            && !await ExistsAsync(tenantId, destination, cancellationToken))
        {
            return new FolderResult(FolderOutcome.NotFound);
        }

        // The tenant predicate is on the UPDATE itself and not on a read before it, so another
        // workspace's file is not «found and then rejected» — it is never matched.
        var affected = await db.StoredFiles
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.FolderId, folderId), cancellationToken);

        return affected == 0
            ? new FolderResult(FolderOutcome.NotFound)
            : new FolderResult(FolderOutcome.Done, folderId);
    }

    /// <summary>Every folder in the workspace, by id. One query, and the basis of every walk above.</summary>
    private async Task<Dictionary<Guid, Folder>> FlatAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Folders
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .ToDictionaryAsync(f => f.Id, cancellationToken);

    /// <summary>
    /// Trimmed, or null when there is nothing left.
    ///
    /// <para>Trimmed rather than rejected for having spaces around it, because a pasted name has
    /// them more often than not. Truncated rather than refused at the length limit for the same
    /// reason a file name is: the column is 512 and a name that long is already unreadable, so the
    /// screen does not need a second sentence about it.</para>
    /// </summary>
    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length > Folder.MaxNameLength ? trimmed[..Folder.MaxNameLength] : trimmed;
    }

    /// <summary>
    /// Whether a sibling already carries that name.
    ///
    /// <para>Case-insensitively, because «Reports» and «reports» side by side in one list is two
    /// folders a customer will put things in at random. Enforced here and not by a unique index: the
    /// index would have to be filtered on a nullable parent, and Postgres and SQLite both count
    /// NULLs as distinct — so every folder at the root would slip through the one constraint that
    /// was supposed to be the backstop. The race this leaves is one person double-submitting the
    /// same name, and the result of losing it is two folders alike, which is untidy rather than
    /// wrong.</para>
    /// </summary>
    private static bool Taken(
        Dictionary<Guid, Folder> all,
        Guid? parentId,
        string name,
        Guid? exceptFolderId) =>
        all.Values.Any(f =>
            f.ParentFolderId == parentId
            && f.Id != exceptFolderId
            && string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private static int DepthOf(Dictionary<Guid, Folder> all, Guid folderId)
    {
        var depth = 0;

        for (var id = (Guid?)folderId; id is { } current && all.TryGetValue(current, out var folder); depth++)
        {
            if (depth > Folder.MaxDepth) break;

            id = folder.ParentFolderId;
        }

        return depth;
    }

    /// <summary>How many levels the deepest thing under this folder adds. A leaf is zero.</summary>
    private static int HeightOf(Dictionary<Guid, Folder> all, Guid folderId)
    {
        var byParent = all.Values.Where(f => f.ParentFolderId is not null).ToLookup(f => f.ParentFolderId!.Value);
        var height = 0;
        var level = new List<Guid> { folderId };

        while (level.Count > 0 && height <= Folder.MaxDepth)
        {
            var next = level.SelectMany(id => byParent[id]).Select(f => f.Id).ToList();
            if (next.Count == 0) break;

            height++;
            level = next;
        }

        return height;
    }

    private static bool IsDescendantOfOrSelf(Dictionary<Guid, Folder> all, Guid candidate, Guid ancestor)
    {
        for (var id = (Guid?)candidate; id is { } current && all.TryGetValue(current, out var folder);)
        {
            if (current == ancestor) return true;

            id = folder.ParentFolderId;
        }

        return false;
    }
}
