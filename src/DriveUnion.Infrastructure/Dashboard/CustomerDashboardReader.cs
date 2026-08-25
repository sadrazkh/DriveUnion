using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Dashboard;

/// <summary>
/// The customer's dashboard, over the readers that already hold most of these numbers.
///
/// <para><b>The two figures the sidebar draws are not recomputed here.</b> Storage against the plan
/// cap comes from <see cref="ITenantPlanService"/> and the trash's size from <see cref="ITrash"/> —
/// the same two services the capacity card reads — so the card above the customer's name and the
/// cards in front of them cannot come to disagree about how full a workspace is or how much of it
/// the trash is holding. The rest is counted here because nothing else counts it.</para>
///
/// <para><b>Every date is judged in memory, and that is not a preference.</b> SQLite keeps a
/// <c>DateTimeOffset</c> as text and refuses both comparison and <c>ORDER BY</c> on one; this layer
/// runs on SQLite in the tests and Postgres in production, and a rule that held in one and not the
/// other would be worse than the read it saved. <c>FileCatalog</c>, <c>TrashPurge</c> and
/// <c>PublicLinkReader</c> each say the same thing at their own call sites. What it costs is written
/// down at each query below rather than left to be discovered.</para>
/// </summary>
public sealed class CustomerDashboardReader(
    DriveUnionDbContext db,
    ITenantPlanService plans,
    ITrash trash,
    ITrafficMeter traffic,
    TimeProvider clock) : ICustomerDashboard
{
    /// <summary>Rows on «آخرین آپلودها». Enough to recognise this week's work, not a second file table.</summary>
    private const int RecentUploadRows = 6;

    private const int BusyLinkRows = 5;

    public async Task<CustomerDashboard?> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetAsync(tenantId, cancellationToken);

        // The claim named a workspace and the row is not there. Null rather than a dashboard of
        // zeroes: a screen that renders zeroes for a workspace that does not exist is how a broken
        // session comes to read as a customer with an empty account.
        if (plan is null) return null;

        var now = clock.GetUtcNow();

        var trashBytes = await trash.SizeAsync(tenantId, cancellationToken);

        // The same predicate SizeAsync uses, over the same (TenantId, DeletedAt) index, asked for a
        // count instead of a sum. An aggregate rather than a materialised list: the trash holds
        // everything deleted inside the retention window, and a workspace that emptied a folder last
        // week would have this page reading every one of those rows to learn how many there are.
        var trashFiles = await db.StoredFiles
            .AsNoTracking()
            .CountAsync(f => f.TenantId == tenantId && f.DeletedAt != null, cancellationToken);

        // One read of this workspace's links, and all four link figures come out of it.
        //
        // «Live» has to be decided here rather than in a WHERE clause, because part of it is an
        // expiry — a DateTimeOffset comparison, which SQLite will not translate. That turns out to be
        // the better arrangement anyway: the decision is ShareLink.Evaluate itself, the very method
        // /d/{slug} refuses on, so the owner's dashboard and the public page cannot come to disagree
        // about which of their links still work. A WHERE clause would have been a second copy of that
        // rule.
        //
        // Bounded by the tenant's own link count, over the (TenantId, CreatedAt) index. It is the
        // same read «لینک‌های اشتراک» already does on every visit.
        var links = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Links that have actually served something. A link at zero downloads is not «busiest», it
        // is «nobody has opened it», and a list padded with those says less than a shorter one.
        // Ordered on an integer, which SQL could have done — but the rows are already in hand.
        var busiest = links
            .Where(l => l.DownloadCount > 0)
            .OrderByDescending(l => l.DownloadCount)
            .ThenBy(l => l.Slug, StringComparer.Ordinal)
            .Take(BusyLinkRows)
            .ToList();

        var busiestFileIds = busiest.Select(l => l.StoredFileId).Distinct().ToList();

        var busiestFileNames = busiestFileIds.Count == 0
            ? []
            : await db.StoredFiles
                .AsNoTracking()
                .Where(f => f.TenantId == tenantId && busiestFileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name })
                .ToDictionaryAsync(f => f.Id, f => f.Name, cancellationToken);

        // The tenant's live files, ordered in memory. Same read and same reason as
        // FileCatalog.ListAsync — SQLite refuses ORDER BY on a DateTimeOffset, so the rows come back
        // and the newest six are taken here. This is the one query on the page whose cost grows with
        // a workspace's library rather than with its links; when paging arrives it is the query to
        // give a keyset to, and the column it would need — an ordering the database can do — is a
        // migration this phase does not make.
        var files = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new RecentUpload(f.Id, f.Name, f.SizeBytes, f.CreatedAt))
            .ToListAsync(cancellationToken);

        // The month this screen is about, from the same UTC clock every row in the product is stamped
        // by — so «this month» starts where the rows say it does rather than where the server stands.
        var spent = await traffic.MonthAsync(tenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);

        return new CustomerDashboard(
            plan,
            spent,
            trashBytes,
            trashFiles,
            links.Count(l => l.Evaluate(now) == ShareLinkAvailability.Available),
            links.Count,

            // The lifetime figure, summed from the counter the public page increments. There is no
            // «this month» beside it: DownloadEvent is indexed on (ShareLinkId, OccurredAt) and a
            // window can only be applied here, in memory, which would mean reading every download
            // this workspace has ever served on the panel's most-visited page. A figure that costs
            // that is not a figure — it is an outage waiting for a busy customer.
            links.Sum(l => (long)l.DownloadCount),

            [.. files.OrderByDescending(f => f.UploadedAt).Take(RecentUploadRows)],
            [
                .. busiest
                    .Where(l => busiestFileNames.ContainsKey(l.StoredFileId))
                    .Select(l => new BusyLink(
                        l.StoredFileId,
                        busiestFileNames[l.StoredFileId],
                        l.Slug,
                        l.DownloadCount,
                        l.MaxDownloads)),
            ]);
    }
}
