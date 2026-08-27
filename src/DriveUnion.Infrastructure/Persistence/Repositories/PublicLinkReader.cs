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

        // The operator has switched this workspace's public half off — see Tenant.PublicSuspendedAt
        // for what that is for. The same card as every other refusal and no reason travelling with
        // it: a visitor who could tell «suspended» from «expired» could tell a live workspace from a
        // dead one by trying a slug, and the reason is the operator's note about somebody else.
        if (await IsSuspendedAsync(file.TenantId, cancellationToken))
        {
            return PublicLinkResolution.NotFound;
        }

        // Read only once the link is known to be available, so a revoked or spent one gives away
        // nothing more than the identical card it already gives. Null for the ordinary file, which
        // is nearly every file, and one query either way.
        var encryption = await db.FileEncryptions
            .AsNoTracking()
            .Where(e => e.StoredFileId == file.Id)
            .Select(e => new EncryptionHeader(
                e.Scheme,
                e.SegmentSize,
                e.NoncePrefix,
                e.PlaintextLength,
                e.KdfSalt,
                e.KdfIterations,
                e.WrappedKey))
            .FirstOrDefaultAsync(cancellationToken);

        if (encryption is not null)
        {
            // The link's own copy of the key, when the owner made one. Three fields replaced and four
            // left alone: the scheme, the segment size, the nonce prefix and the plaintext length
            // describe the ciphertext that is actually on disk, and swapping any of them for a
            // link's would be describing a different file.
            //
            // Absent, and the file's own wrapped key travels instead — which is what shipped with
            // the format and is still correct: the recipient needs the owner's passphrase, and the
            // panel said so when the link was made.
            var linkKey = await db.ShareLinkKeys
                .AsNoTracking()
                .Where(k => k.ShareLinkId == link.Id)
                .Select(k => new LinkKeyMaterial(k.KdfSalt, k.KdfIterations, k.WrappedKey))
                .FirstOrDefaultAsync(cancellationToken);

            if (linkKey is not null)
            {
                encryption = encryption with
                {
                    KdfSalt = linkKey.KdfSalt,
                    KdfIterations = linkKey.KdfIterations,
                    WrappedKey = linkKey.WrappedKey,
                };
            }
        }

        // The name on the card. Read by the file's tenant rather than the link's — they are the same
        // row and always have been, and the file is what is being shown.
        var sharedBy = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == file.TenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

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
                link.ExpiresAt,
                encryption,
                sharedBy,
                link.Note,
                Previews.For(file.MimeType, file.SizeBytes, encryption is not null)));
    }

    public async Task<PublicDownloadTicket?> ResolveForDownloadAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var (link, file) = await LookUpAsync(slug, cancellationToken);

        if (link is null || file is null) return null;
        if (link.Evaluate(clock.GetUtcNow()) != ShareLinkAvailability.Available) return null;

        // Asked again on the streaming path rather than trusted from the page that linked here. The
        // two routes are reached independently — a visitor holding a direct /file address never
        // loads the card — so a suspension enforced only on the page would stop the button and leave
        // the bytes served.
        if (await IsSuspendedAsync(file.TenantId, cancellationToken)) return null;

        return new PublicDownloadTicket(
            link.Id,
            file.TenantId,
            file.GoogleAccountId,
            file.DriveFileId,
            file.Name,
            file.MimeType,
            file.SizeBytes,
            await db.FileEncryptions.AnyAsync(e => e.StoredFileId == file.Id, cancellationToken));
    }

    public async Task<bool> TryReserveDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken)
    {
        // One conditional UPDATE: the test and the increment are the same statement, so the database
        // decides who gets the last slot. Reading the count and then writing count + 1 hands 500 of
        // 500 to both of two requests that read 499, and asking "is there room?" in one statement and
        // taking it in another leaves a gap the length of a download — hours, on a 214 GB file.
        //
        // Rows affected is the answer. One means this request owns a slot; zero means the link is
        // revoked or spent, and the caller cannot tell those apart, which is deliberate.
        //
        // Expiry is deliberately not in the predicate. SQLite keeps a DateTimeOffset as text and will
        // not compare one — the same reason ShareLinkService sorts in memory — so a WHERE on ExpiresAt
        // would mean one rule on Postgres and a different one under the tests. It costs nothing: the
        // resolve one round trip earlier evaluates expiry against the clock, and unlike the cap,
        // expiry is not a value two anonymous requests can take from each other.
        var affected = await db.ShareLinks
            .Where(l => l.Id == shareLinkId
                && l.IsActive
                && (l.MaxDownloads == null || l.DownloadCount < l.MaxDownloads))
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.DownloadCount, l => l.DownloadCount + 1),
                cancellationToken);

        if (affected == 0) return false;

        DetachStaleCount(shareLinkId);

        return true;
    }

    public async Task RecordDownloadAsync(
        Guid shareLinkId,
        string ipHash,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        // The counter is not touched here. It moved when the slot was reserved, and a second
        // increment at the last byte would bill the download twice.
        var linkExists = await db.ShareLinks
            .AsNoTracking()
            .AnyAsync(l => l.Id == shareLinkId, cancellationToken);

        if (!linkExists)
        {
            // The link was deleted between the reservation and the last byte. DownloadEvent has a
            // foreign key to it, so the insert would fail; an orphaned audit row helps nobody either.
            return;
        }

        db.DownloadEvents.Add(new DownloadEvent
        {
            Id = Guid.NewGuid(),
            ShareLinkId = shareLinkId,
            OccurredAt = clock.GetUtcNow(),
            IpHash = ipHash,
            UserAgent = userAgent,
        });

        // One row, so SaveChanges is already all-or-nothing and there is nothing for a transaction to
        // hold together. What the counter and the audit row no longer share is a moment: the count
        // moves when the download starts and the row lands when it finishes, which is the whole point
        // of reserving.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken)
    {
        // The floor lives in the WHERE rather than in the new value, so it is applied by the same
        // statement that does the decrement. A release that computed Math.Max(0, count - 1) from a
        // value it read would still write -1 when two of them overlap at a count of one, and a
        // negative counter is free downloads for as long as it takes somebody to notice.
        var affected = await db.ShareLinks
            .Where(l => l.Id == shareLinkId && l.DownloadCount > 0)
            .ExecuteUpdateAsync(
                s => s.SetProperty(l => l.DownloadCount, l => l.DownloadCount - 1),
                cancellationToken);

        if (affected > 0) DetachStaleCount(shareLinkId);
    }

    /// <summary>
    /// ExecuteUpdate goes round the change tracker, so a copy someone else loaded still holds the old
    /// count. Left attached, the next SaveChanges in this scope — the audit row at the end of the
    /// very transfer this reserved for — would write that stale number back over the move.
    /// </summary>
    private void DetachStaleCount(Guid shareLinkId)
    {
        var stale = db.ChangeTracker.Entries<ShareLink>()
            .FirstOrDefault(e => e.Entity.Id == shareLinkId);

        if (stale is not null) stale.State = EntityState.Detached;
    }

    /// <summary>
    /// Whether the operator has switched this workspace's public half off.
    ///
    /// <para>A tenant appears on this anonymous path for the same reason it does on the download
    /// ticket: the file that was found belongs to somebody, and this is a fact about them rather
    /// than about the visitor. Nothing about resolving a slug is scoped by workspace.</para>
    /// </summary>
    private async Task<bool> IsSuspendedAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.PublicSuspendedAt != null, cancellationToken);

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
