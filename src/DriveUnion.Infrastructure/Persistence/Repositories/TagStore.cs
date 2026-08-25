using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The workspace's labels.
///
/// <para>Every query carries <c>tenantId</c>, including the ones over the join table — see
/// <see cref="FileTag"/> for why the tenant is on that row at all.</para>
/// </summary>
public sealed class TagStore(DriveUnionDbContext db, TimeProvider clock) : ITags
{
    public async Task<IReadOnlyList<TagSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tags = await db.Tags
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new TagSummary(
                t.Id,
                t.Name,

                // Live files only. A tag whose files are all in the trash counts zero, because
                // pressing it would list nothing and a count that promised otherwise would be a
                // number the reader cannot see the parts of.
                db.FileTags.Count(ft => ft.TagId == t.Id
                    && db.StoredFiles.Any(f => f.Id == ft.StoredFileId && f.DeletedAt == null))))
            .ToListAsync(cancellationToken);

        // Sorted here rather than in SQL, for the reason FolderTree gives: collation decides what
        // ORDER BY means for a Persian name and the two providers do not agree.
        return [.. tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<TagResult> EnsureAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        if (Clean(name) is not { } cleaned) return new TagResult(TagOutcome.NameEmpty);

        var existing = await db.Tags
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);

        // Matched case-insensitively in memory rather than with a WHERE, so «فوری» and «فوری» typed
        // with different casing land on one tag whichever provider is underneath. The list is at
        // most MaxPerTenant long.
        var match = existing.FirstOrDefault(t =>
            string.Equals(t.Name, cleaned, StringComparison.CurrentCultureIgnoreCase));

        if (match is not null) return new TagResult(TagOutcome.Done, match.Id);

        if (existing.Count >= Tag.MaxPerTenant) return new TagResult(TagOutcome.TooMany);

        var tag = new Tag
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = cleaned,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Tags.Add(tag);
        await db.SaveChangesAsync(cancellationToken);

        return new TagResult(TagOutcome.Done, tag.Id);
    }

    public async Task<TagResult> ApplyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new TagResult(TagOutcome.Done);

        if (!await db.Tags.AnyAsync(t => t.Id == tagId && t.TenantId == tenantId, cancellationToken))
        {
            return new TagResult(TagOutcome.NotFound);
        }

        // The files this workspace actually has, live. Filtering here rather than trusting the ids
        // is what stops a hand-made form putting one workspace's tag on another's file — the same
        // reasoning as the tenant predicate on every UPDATE in this layer.
        var mine = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null && fileIds.Contains(f.Id))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        var already = await db.FileTags
            .AsNoTracking()
            .Where(ft => ft.TenantId == tenantId && ft.TagId == tagId && mine.Contains(ft.StoredFileId))
            .Select(ft => ft.StoredFileId)
            .ToListAsync(cancellationToken);

        var fresh = mine.Except(already).ToList();

        if (fresh.Count == 0) return new TagResult(TagOutcome.Done, tagId);

        db.FileTags.AddRange(fresh.Select(id => new FileTag
        {
            StoredFileId = id,
            TagId = tagId,
            TenantId = tenantId,
        }));

        await db.SaveChangesAsync(cancellationToken);

        return new TagResult(TagOutcome.Done, tagId, fresh.Count);
    }

    public async Task<TagResult> RemoveAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new TagResult(TagOutcome.Done);

        var affected = await db.FileTags
            .Where(ft => ft.TenantId == tenantId && ft.TagId == tagId && fileIds.Contains(ft.StoredFileId))
            .ExecuteDeleteAsync(cancellationToken);

        return new TagResult(TagOutcome.Done, tagId, affected);
    }

    public async Task<TagResult> DeleteAsync(Guid tenantId, Guid tagId, CancellationToken cancellationToken)
    {
        var affected = await db.FileTags
            .Where(ft => ft.TenantId == tenantId && ft.TagId == tagId)
            .ExecuteDeleteAsync(cancellationToken);

        var removed = await db.Tags
            .Where(t => t.Id == tagId && t.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

        return removed == 0
            ? new TagResult(TagOutcome.NotFound)
            : new TagResult(TagOutcome.Done, tagId, affected);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TagSummary>>> ForFilesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<TagSummary>>();

        // One query for a whole page of rows. A tag list per row would be a query per row, on the
        // panel's most-visited screen.
        var pairs = await db.FileTags
            .AsNoTracking()
            .Where(ft => ft.TenantId == tenantId && fileIds.Contains(ft.StoredFileId))
            .Join(
                db.Tags.Where(t => t.TenantId == tenantId),
                ft => ft.TagId,
                t => t.Id,
                (ft, t) => new { ft.StoredFileId, t.Id, t.Name })
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(p => p.StoredFileId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TagSummary>)
                [
                    .. g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(p => new TagSummary(p.Id, p.Name, 0)),
                ]);
    }

    /// <summary>Trimmed, truncated to the column, or null when there is nothing left.</summary>
    private static string? Clean(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length > Tag.MaxNameLength ? trimmed[..Tag.MaxNameLength] : trimmed;
    }
}
