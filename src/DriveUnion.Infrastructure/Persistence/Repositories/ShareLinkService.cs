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

        if (request.Key is { } key && !key.IsWellFormed)
        {
            // Before the slug is allocated, so a refused re-wrap leaves no link behind at all.
            // Storing it would make a link that resolves, renders and asks for a secret that cannot
            // possibly work — and the owner would find out from the person they sent it to.
            throw new ArgumentException(
                "The re-wrapped key is not shaped like one.", nameof(request));
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

            if (request.Key is { } material)
            {
                // Same SaveChanges as the link. A link that exists without the key it was made for
                // is one that hands out the owner's own wrapped key instead — silently widening what
                // the recipient can open, which is the exact thing this feature exists to stop.
                db.ShareLinkKeys.Add(new ShareLinkKey
                {
                    ShareLinkId = link.Id,
                    TenantId = tenantId,
                    KdfSalt = material.KdfSalt,
                    KdfIterations = material.KdfIterations,
                    WrappedKey = material.WrappedKey,
                    CreatedAt = now,
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);

                return new ShareLinkSummary(
                    link.Id, link.Slug, link.ExpiresAt, link.MaxDownloads, link.DownloadCount,
                    link.IsActive, link.Note, request.Key is not null);
            }
            catch (DbUpdateException) when (attempt < MaxSlugAttempts)
            {
                // The failed insert is still tracked as Added; leaving it would replay the same
                // colliding slug on the next SaveChanges. The key row goes with it — it names the
                // link id that is being abandoned, and a retry builds a fresh one.
                db.Entry(link).State = EntityState.Detached;

                foreach (var orphan in db.ChangeTracker.Entries<ShareLinkKey>()
                    .Where(e => e.Entity.ShareLinkId == link.Id)
                    .ToList())
                {
                    orphan.State = EntityState.Detached;
                }

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
                l.Note,
                // Whether it has one, never what it is. A listing draws a word; the wrapped key has
                // no business on a screen that needed one bit — the same rule the padlock follows.
                db.ShareLinkKeys.Any(k => k.ShareLinkId == l.Id)))
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
                        l.CreatedAt, l.Note,
                        db.ShareLinkKeys.Any(k => k.ShareLinkId == l.Id)),
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

    public async Task<ShareLinkEdit> UpdateAsync(
        Guid tenantId,
        Guid linkId,
        DateTimeOffset? expiresAt,
        int? maxDownloads,
        string? note,
        CancellationToken cancellationToken)
    {
        // Read first, and only to compare the new ceiling against what has already been spent. It
        // decides nothing about permission: the UPDATE below carries the tenant predicate too, so
        // another workspace's link is not «found and rejected» there either.
        //
        // Revoked links are excluded here and in the write. Revoking burns a slug for ever, and an
        // edit that could revive one would be an undo for the one action in this product that has
        // none — see IShareLinkService.UpdateAsync.
        var spent = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.Id == linkId && l.TenantId == tenantId && l.IsActive)
            .Select(l => (int?)l.DownloadCount)
            .FirstOrDefaultAsync(cancellationToken);

        if (spent is not { } already) return ShareLinkEdit.NotFound;

        // Refused rather than clamped or accepted: accepting kills a live link on the spot in a way
        // nobody asked for, and clamping stores a number they did not type. The screen says both.
        if (maxDownloads is { } ceiling && ceiling < already) return ShareLinkEdit.BelowWhatIsSpent;

        var affected = await db.ShareLinks
            .Where(l => l.Id == linkId && l.TenantId == tenantId && l.IsActive)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(l => l.ExpiresAt, expiresAt)
                    .SetProperty(l => l.MaxDownloads, maxDownloads)
                    .SetProperty(l => l.Note, Trimmed(note)),
                cancellationToken);

        return affected > 0 ? ShareLinkEdit.Changed : ShareLinkEdit.NotFound;
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
