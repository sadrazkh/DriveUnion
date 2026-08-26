using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The owner's side of a link: create, list, revoke — all scoped to one tenant by an explicit
/// argument.
/// </summary>
public sealed class ShareLinkService(
    DriveUnionDbContext db,
    ISlugGenerator slugs,
    TimeProvider clock) : IShareLinkService
{
    /// <summary>
    /// 36^8 slugs make a collision vanishingly unlikely, and five attempts make it impossible to
    /// notice. The number is small on purpose: if five CSPRNG draws in a row collide, the generator
    /// is broken and the exception should say so rather than being retried into a timeout.
    /// </summary>
    private const int MaxSlugAttempts = 5;

    public async Task<ShareLinkSummary> CreateAsync(
        Guid tenantId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken)
    {
        var fileExists = await db.StoredFiles
            .AsNoTracking()
            .AnyAsync(
                f => f.Id == request.StoredFileId && f.TenantId == tenantId && f.DeletedAt == null,
                cancellationToken);

        if (!fileExists)
        {
            // Another tenant's file and a file that never existed produce the same refusal, for the
            // same reason the public page has one card: a distinguishable "not yours" is a probe.
            throw new KeyNotFoundException($"File {request.StoredFileId} was not found.");
        }

        var now = clock.GetUtcNow();

        for (var attempt = 1; attempt <= MaxSlugAttempts; attempt++)
        {
            var link = new ShareLink
            {
                Id = Guid.NewGuid(),
                Slug = slugs.Next(),
                StoredFileId = request.StoredFileId,
                TenantId = tenantId,
                ExpiresAt = request.ExpiresAt,
                MaxDownloads = request.MaxDownloads,
                // Cut rather than refused: this is a sentence typed into a box beside a button, and
                // losing the link because the sentence ran long would be the wrong trade. Trimmed to
                // null so an empty box and a box full of spaces are both «no note» — the page tests
                // one thing rather than two.
                Note = Trimmed(request.Note),
                DownloadCount = 0,
                IsActive = true,
                CreatedAt = now,
            };

            db.ShareLinks.Add(link);

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return new ShareLinkSummary(
                    link.Id, link.Slug, link.ExpiresAt, link.MaxDownloads, link.DownloadCount,
                    link.IsActive, link.Note);
            }
            catch (DbUpdateException) when (attempt < MaxSlugAttempts)
            {
                // The failed insert is still tracked as Added; leaving it would replay the same
                // colliding slug on the next SaveChanges.
                db.Entry(link).State = EntityState.Detached;

                // Which constraint fired is a provider-specific error code, and this product runs on
                // Postgres in production and SQLite in the tests. Asking whether the slug is now
                // taken answers the same question without either dialect: if it is not, the failure
                // was something else and retrying with a fresh slug would bury it.
                var slugTaken = await db.ShareLinks
                    .AsNoTracking()
                    .AnyAsync(l => l.Slug == link.Slug, cancellationToken);

                if (!slugTaken) throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not allocate a free share slug in {MaxSlugAttempts} attempts.");
    }

    public async Task<IReadOnlyList<ShareLinkSummary>> ListForFileAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var links = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.StoredFileId == fileId)
            .Select(l => new LinkRow(
                l.Id, l.Slug, l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.IsActive, l.CreatedAt,
                l.Note))
            .ToListAsync(cancellationToken);

        // Newest first, sorted in memory: SQLite refuses ORDER BY on a DateTimeOffset and this code
        // has to behave the same on it as on Postgres. See FileCatalog.ListAsync.
        return [.. links.OrderByDescending(l => l.CreatedAt).Select(l => l.ToSummary())];
    }

    public async Task<IReadOnlyList<(ShareLinkSummary Link, Guid StoredFileId, string FileName)>>
        ListForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            // An inner join, so a link whose file has been soft-deleted drops out. That is not a
            // hidden row: FileCatalog.DeleteAsync revokes a file's links as it deletes it, so what
            // would be listed is a dead link naming a file the tenant already removed.
            .Join(
                db.StoredFiles.Where(f => f.TenantId == tenantId && f.DeletedAt == null),
                l => l.StoredFileId,
                f => f.Id,
                (l, f) => new
                {
                    Row = new LinkRow(
                        l.Id, l.Slug, l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.IsActive,
                        l.CreatedAt, l.Note),
                    FileId = f.Id,
                    f.Name,
                })
            .ToListAsync(cancellationToken);

        // Newest first, sorted in memory: SQLite refuses ORDER BY on a DateTimeOffset and this code
        // has to behave the same on it as on Postgres. See ListForFileAsync.
        return [.. rows
            .OrderByDescending(r => r.Row.CreatedAt)
            .Select(r => (r.Row.ToSummary(), r.FileId, r.Name))];
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken)
    {
        var affected = await db.ShareLinks
            .Where(l => l.Id == linkId && l.TenantId == tenantId && l.IsActive)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.IsActive, false),
                cancellationToken);

        return affected > 0;
    }

    /// <summary>
    /// The note as it will be stored: trimmed, cut to the column, or null.
    ///
    /// <para>Null and not an empty string, so «no note» is one state rather than two and the page
    /// has one thing to test. Cut rather than refused — see <see cref="CreateShareLinkRequest"/>.
    /// </para>
    /// </summary>
    private static string? Trimmed(string? note)
    {
        if (note?.Trim() is not { Length: > 0 } typed) return null;

        return typed.Length <= ShareLink.MaxNoteLength
            ? typed
            : typed[..ShareLink.MaxNoteLength];
    }
}
