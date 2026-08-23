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
                DownloadCount = 0,
                IsActive = true,
                CreatedAt = now,
            };

            db.ShareLinks.Add(link);

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return new ShareLinkSummary(
                    link.Id, link.Slug, link.ExpiresAt, link.MaxDownloads, link.DownloadCount, link.IsActive);
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
                l.Id, l.Slug, l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.IsActive, l.CreatedAt))
            .ToListAsync(cancellationToken);

        // Newest first, sorted in memory: SQLite refuses ORDER BY on a DateTimeOffset and this code
        // has to behave the same on it as on Postgres. See FileCatalog.ListAsync.
        return [.. links.OrderByDescending(l => l.CreatedAt).Select(l => l.ToSummary())];
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
}
