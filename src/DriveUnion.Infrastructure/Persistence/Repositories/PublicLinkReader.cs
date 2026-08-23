using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// The anonymous path: /d/{slug} and the stream behind it.
///
/// There is no tenant argument on any method here and there must never be one. /d/{slug} arrives
/// with no signed-in user, so a tenant taken from the request would be <c>Guid.Empty</c> and every
/// live link in the product would answer "not found" while its row sat plainly in the table — a
/// failure that reads like a routing bug and that no test which signs in first can see.
/// </summary>
public sealed class PublicLinkReader(DriveUnionDbContext db, TimeProvider clock) : IPublicLinkReader
{
    public async Task<PublicLinkResolution> ResolveAsync(string slug, CancellationToken cancellationToken)
    {
        var (link, file) = await LookUpAsync(slug, cancellationToken);

        if (link is null) return PublicLinkResolution.NotFound;

        var availability = link.Evaluate(clock.GetUtcNow());
        if (availability != ShareLinkAvailability.Available)
        {
            // The reason travels for the logs and the owner's panel. The file does not: a refusing
            // link that still returned a file name would render a different card from an unknown
            // slug, and the difference is all an enumerator needs.
            return new PublicLinkResolution(false, availability, null);
        }

        // A link can outlive its file only if something bypassed IFileCatalog.DeleteAsync, which
        // revokes links as it soft-deletes. There is nothing to serve either way.
        if (file is null) return PublicLinkResolution.NotFound;

        return new PublicLinkResolution(
            true,
            ShareLinkAvailability.Available,
            new PublicFileView(
                link.Slug,
                file.Name,
                file.MimeType,
                file.SizeBytes,
                link.CreatedAt,
                link.DownloadCount,
                link.MaxDownloads,
                link.ExpiresAt));
    }

    public async Task<PublicDownloadTicket?> ResolveForDownloadAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var (link, file) = await LookUpAsync(slug, cancellationToken);

        if (link is null || file is null) return null;
        if (link.Evaluate(clock.GetUtcNow()) != ShareLinkAvailability.Available) return null;

        return new PublicDownloadTicket(
            link.Id,
            file.GoogleAccountId,
            file.DriveFileId,
            file.Name,
            file.MimeType,
            file.SizeBytes);
    }

    public async Task RecordDownloadAsync(
        Guid shareLinkId,
        string ipHash,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        // One UPDATE, and the new value is computed by the database from the old one. Read-modify-
        // write loses a download every time two of them overlap, and at 499/500 it hands out a
        // 501st.
        var affected = await db.ShareLinks
            .Where(l => l.Id == shareLinkId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.DownloadCount, l => l.DownloadCount + 1),
                cancellationToken);

        if (affected == 0)
        {
            // The link was deleted between the ticket and the last byte. There is no counter to move
            // and no audit row worth orphaning.
            return;
        }

        // ExecuteUpdate goes round the change tracker, so a copy someone else loaded still holds the
        // old count. Left attached, the next SaveChanges in this scope would write that stale number
        // back over the increment we just made.
        var stale = db.ChangeTracker.Entries<ShareLink>()
            .FirstOrDefault(e => e.Entity.Id == shareLinkId);
        if (stale is not null) stale.State = EntityState.Detached;

        db.DownloadEvents.Add(new DownloadEvent
        {
            Id = Guid.NewGuid(),
            ShareLinkId = shareLinkId,
            OccurredAt = clock.GetUtcNow(),
            IpHash = ipHash,
            UserAgent = userAgent,
        });

        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task<(ShareLink? Link, StoredFile? File)> LookUpAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        // A malformed slug cannot match a generated one, so it is answered without a query. Both
        // paths end at the same card, so this leaks nothing beyond the shape the generator already
        // publishes in every URL.
        if (!SlugGenerator.IsWellFormed(slug)) return (null, null);

        var link = await db.ShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Slug == slug, cancellationToken);

        if (link is null) return (null, null);

        var file = await db.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == link.StoredFileId && f.DeletedAt == null,
                cancellationToken);

        return (link, file);
    }
}
