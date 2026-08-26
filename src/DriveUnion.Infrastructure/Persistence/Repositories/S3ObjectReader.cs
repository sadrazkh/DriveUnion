using System.Globalization;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Object keys over the folder tree. The mapping and its costs are on <see cref="IS3Objects"/>.
/// </summary>
public sealed class S3ObjectReader(DriveUnionDbContext db, IFolderTree folders) : IS3Objects
{
    public async Task<S3Listing> ListAsync(
        Guid tenantId,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        // The whole workspace's paths, built once. A listing has to compare keys, and a key is a
        // folder path — there is no query that produces one, so the tree is read and the paths are
        // assembled here. Tens of folders; the alternative is a query per file.
        var paths = await PathsAsync(tenantId, cancellationToken);

        var files = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new { f.Id, f.Name, f.SizeBytes, f.ModifiedAt, f.FolderId })
            .ToListAsync(cancellationToken);

        var keyed = files
            .Select(f => new S3Object(
                f.FolderId is { } id && paths.TryGetValue(id, out var path) ? $"{path}/{f.Name}" : f.Name,
                f.SizeBytes,
                f.ModifiedAt,

                // An ETag is required and this one is not an MD5 of the content: computing that
                // would mean reading every object out of Drive to answer a listing. It is stable
                // per version of a file, which is what a client uses it for — telling whether the
                // thing it has is the thing that is there. Quoted, because S3's is.
                $"\"{Etag(f.Id, f.SizeBytes, f.ModifiedAt)}\""))
            .Where(o => prefix is not { Length: > 0 } || o.Key.StartsWith(prefix, StringComparison.Ordinal))

            // Ordinal, because S3 lists by byte order and a client paging through with a
            // continuation token relies on the order being the same on the next request.
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .ToList();

        // The continuation token is the last key that was returned, so «where was I» is a comparison
        // rather than an offset — an object added or removed between two pages shifts an offset and
        // makes a client skip or repeat one.
        if (continuationToken is { Length: > 0 })
        {
            keyed = [.. keyed.Where(o => string.CompareOrdinal(o.Key, continuationToken) > 0)];
        }

        var objects = new List<S3Object>();
        var prefixes = new List<string>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        string? next = null;

        foreach (var candidate in keyed)
        {
            if (objects.Count + prefixes.Count >= maxKeys)
            {
                truncated = true;
                break;
            }

            // With a delimiter, everything below the first one after the prefix collapses into a
            // single «directory» entry. That is how `aws s3 ls` shows folders over a store that,
            // as far as S3 is concerned, has none.
            if (delimiter is { Length: > 0 })
            {
                var after = prefix is { Length: > 0 } ? candidate.Key[prefix.Length..] : candidate.Key;
                var cut = after.IndexOf(delimiter, StringComparison.Ordinal);

                if (cut >= 0)
                {
                    var common = (prefix ?? string.Empty) + after[..(cut + delimiter.Length)];

                    if (seenPrefixes.Add(common)) prefixes.Add(common);

                    next = candidate.Key;
                    continue;
                }
            }

            objects.Add(candidate);
            next = candidate.Key;
        }

        return new S3Listing(objects, prefixes, truncated, truncated ? next : null);
    }

    public async Task<S3Located?> LocateAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var (folderPath, name) = Split(key);

        if (name.Length == 0) return null;

        var folderId = folderPath.Length == 0
            ? null
            : (await PathsAsync(tenantId, cancellationToken))
                .Where(p => p.Value == folderPath)
                .Select(p => (Guid?)p.Key)
                .FirstOrDefault();

        if (folderPath.Length > 0 && folderId is null) return null;

        var matches = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null && f.FolderId == folderId && f.Name == name)
            .Select(f => new S3Located(f.Id, f.FolderId, f.Name, f.SizeBytes, f.ModifiedAt))
            .ToListAsync(cancellationToken);

        // The most recent, because a key is unique in S3 and a name is not here — see IS3Objects.
        return matches.OrderByDescending(m => m.ModifiedAt).FirstOrDefault();
    }

    public async Task<(Guid? FolderId, FolderOutcome Refused)> EnsurePathAsync(
        Guid tenantId,
        Guid ownerUserId,
        string key,
        CancellationToken cancellationToken)
    {
        var (folderPath, _) = Split(key);

        if (folderPath.Length == 0) return (null, FolderOutcome.Done);

        Guid? parent = null;

        foreach (var segment in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = (await folders.ChildrenAsync(tenantId, parent, cancellationToken))
                .FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.CurrentCultureIgnoreCase));

            if (existing is not null)
            {
                parent = existing.Id;
                continue;
            }

            var made = await folders.CreateAsync(tenantId, ownerUserId, parent, segment, cancellationToken);

            // NameTaken cannot happen — the children were just checked — but a race would produce
            // it, and re-reading is cheaper and more honest than assuming.
            if (!made.Succeeded && made.Outcome != FolderOutcome.NameTaken) return (null, made.Outcome);

            parent = made.FolderId ?? (await folders.ChildrenAsync(tenantId, parent, cancellationToken))
                .First(f => string.Equals(f.Name, segment, StringComparison.CurrentCultureIgnoreCase)).Id;
        }

        return (parent, FolderOutcome.Done);
    }

    /// <summary>Every folder in the workspace as a path, by id.</summary>
    private async Task<Dictionary<Guid, string>> PathsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var choices = await folders.ChoicesAsync(tenantId, excludingSubtreeOf: null, cancellationToken);

        // ChoicesAsync writes « / » between segments for a human reading a picker; a key uses a bare
        // slash. Converted here rather than adding a second walk to the tree.
        return choices.ToDictionary(c => c.Id, c => c.Path.Replace(" / ", "/", StringComparison.Ordinal));
    }

    /// <summary>The folder path and the file name either side of the last slash.</summary>
    private static (string FolderPath, string Name) Split(string key)
    {
        var trimmed = key?.Trim('/') ?? string.Empty;
        var cut = trimmed.LastIndexOf('/');

        return cut < 0 ? (string.Empty, trimmed) : (trimmed[..cut], trimmed[(cut + 1)..]);
    }

    private static string Etag(Guid id, long size, DateTimeOffset modified) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Create(CultureInfo.InvariantCulture, $"{id:N}:{size}:{modified.UtcTicks}"))));
}
