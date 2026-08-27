using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Dashboard;

/// <summary>
/// The operator's dashboard: the pool, the workspaces pressing against their ceilings, and whatever
/// is failing.
///
/// <para><b>Aggregates and pool rows, and no customer's file ever.</b> There is no method here that
/// returns one and there must not be — an operator inspecting a customer's files does it through the
/// same tenant-scoped catalogue the customer's own request calls, with the tenant from the route.
/// </para>
///
/// <para><b>Why this does not call <see cref="IOperatorPlanReader"/>.</b> That reader answers the
/// same question about commitment and it groups the whole <c>StoredFiles</c> table to do it, because
/// its screen prints a file count per workspace. This one is the panel's home page and does not; the
/// commitment figure is a sum of a column on <c>Tenants</c>, which is one row per customer. Reusing
/// the richer reader here would put a full scan of the file table on every dashboard render to
/// produce a number this screen does not draw.</para>
/// </summary>
public sealed class OperatorDashboardReader(
    DriveUnionDbContext db,
    IGoogleAccountDirectory accounts,
    ITrafficMeter traffic,
    TimeProvider clock) : IOperatorDashboard
{
    /// <summary>
    /// How far back «what has failed» reaches. A day, because that is the span an operator is asked
    /// about — a failure from last week is history, and history belongs to a log.
    /// </summary>
    public const int FailureWindowHours = 24;

    /// <summary>
    /// How far back the egress chart reaches, in days, counting today.
    ///
    /// <para><b>Thirty days and not this calendar month.</b> The question the chart answers is «what
    /// is the trend right now», and a calendar month answers it with one column on the first and two
    /// on the second — the operator's screen would be least useful in the week they most want to
    /// know whether something changed at the turn of the month. A rolling window is the same width
    /// every day.</para>
    ///
    /// <para><b>And daily buckets, because that is already the grain of the table.</b>
    /// <c>TenantUsageDay</c> is a per-workspace-per-day roll-up written for exactly this reason, so a
    /// day needs no re-bucketing and cannot come to disagree with the figure the customer's own card
    /// draws. Anything finer does not exist to be drawn; anything coarser — weeks — would put six
    /// columns on the screen and hide the spike that is the only thing worth seeing.</para>
    ///
    /// <para>It is also what the query costs: the rows are grouped by day in SQL, so at most thirty
    /// come back however many customers there are.</para>
    /// </summary>
    public const int EgressWindowDays = 30;

    /// <summary>
    /// The percentage at which a workspace joins the «near their ceiling» list.
    ///
    /// <para>The same eighty the progress bars turn amber at, so a workspace that is on this list is
    /// exactly a workspace whose bar has changed colour. Two ladders would let the screen list
    /// somebody it drew in green.</para>
    /// </summary>
    public const double CeilingPercent = 80d;

    private const int PressureRows = 5;

    private const int FailureRows = 5;

    public async Task<OperatorDashboard> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-FailureWindowHours);

        // The pool, through the directory that already owns it. Two or three rows, and reusing it
        // means the dashboard's cards and the accounts screen's cards read one source.
        var pool = await accounts.ListAsync(cancellationToken);

        var cards = pool
            .Select(a => new PoolAccount(
                a.Id, a.Email, a.Label, a.Status, a.QuotaUsedBytes, a.QuotaTotalBytes))
            .ToList();

        // A disconnected account is not capacity — nothing can be written to it — so it is out of
        // both totals. The same rule OperatorPlanReader applies, for the same reason: counting it
        // would make the pool look larger than it is on exactly the day that matters.
        var connected = cards.Where(a => a.Status != GoogleAccountStatus.Disconnected).ToList();

        // Every workspace, with the two columns this screen needs. It is the operator's customer
        // book — one row per customer — and it is read whole rather than ordered in SQL because the
        // ordering is by a ratio, and a ratio in SQL is a division whose denominator an override can
        // set to anything. OperatorTenantDirectory reads the same table the same way.
        //
        // At a few thousand customers this wants to become an ordered, bounded query with the
        // divide-by-zero written out; it is one row per paying customer and is nowhere near that.
        var workspaces = await db.Tenants
            .AsNoTracking()
            .Select(t => new WorkspacePressure(t.Id, t.Name, t.StorageUsedBytes, t.StorageQuotaBytes))
            .ToListAsync(cancellationToken);

        var pressing = workspaces
            .Where(w => Percent(w) >= CeilingPercent)
            .OrderByDescending(Percent)
            .ThenBy(w => w.Name, StringComparer.Ordinal)
            .Take(PressureRows)
            .ToList();

        // The two states this screen is about, in one read, judged here rather than in SQL.
        //
        // Both halves need a date — «still resumable» is an expiry and «failed today» is a window —
        // and SQLite will neither compare nor ORDER BY a DateTimeOffset. This layer runs on SQLite in
        // the tests and Postgres in production, so the dates are decided in memory, which is the same
        // answer FileCatalog, TrashPurge and PublicLinkReader each reached at their own call sites.
        //
        // What the database still does is the part it is good at: the status filter. That leaves the
        // read bounded by «uploads in flight» — which is small, because they terminate — plus
        // «uploads that have ever failed», which is small because failing is exceptional and is the
        // set an operator came here to look at. Nothing sweeps completed sessions and none of them is
        // read here. If failures ever accumulate faster than they are looked at, the fix is an index
        // on Status and a sweeper for terminal rows; both are migrations, and neither is this phase's.
        var sessions = await db.UploadSessions
            .AsNoTracking()
            .Where(u => u.Status == UploadSessionStatus.InProgress
                || u.Status == UploadSessionStatus.Failed)
            .Select(u => new
            {
                u.Id,
                u.Status,
                u.FileName,
                u.SizeBytes,
                u.CreatedAt,
                u.ExpiresAt,
                u.FailureReason,
            })
            .ToListAsync(cancellationToken);

        // In flight means Google would still take a chunk for it. An expired session is not in
        // flight — its resumable URI is dead and the browser has to start over — so counting it
        // would leave a permanent number on a card whose whole job is to read zero when nothing is
        // happening.
        var inFlight = sessions.Count(
            s => s.Status == UploadSessionStatus.InProgress && s.ExpiresAt > now);

        // Ordered by when the session started rather than by when it failed, because nothing records
        // the second: a session carries a CreatedAt and a status, and inventing a failure time from
        // the row's last write is not something the schema can honestly support.
        var recent = sessions
            .Where(s => s.Status == UploadSessionStatus.Failed && s.CreatedAt >= since)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        var failed = recent.Count;

        // The card shows a handful and the count above says how many there are, so the list stays
        // bounded and the figure above it stays the truth.
        var failures = recent
            .Take(FailureRows)
            .Select(s => new FailedTransfer(s.Id, s.FileName, s.SizeBytes, s.CreatedAt, s.FailureReason))
            .ToList();

        // The egress window, in UTC days, because that is the clock TenantUsageDay.Day is stamped in.
        // Reading «today» in the server's own zone would shift every column by one on a box that is
        // not on UTC, and the chart would disagree with the customer's card about which day is which.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = today.AddDays(-(EgressWindowDays - 1));

        var egress = FillTheQuietDays(
            await traffic.EveryTenantRangeAsync(from, today, cancellationToken),
            from,
            today);

        return new OperatorDashboard(
            cards,
            connected.Sum(a => a.QuotaUsedBytes),
            connected.Sum(a => a.QuotaTotalBytes),
            cards.Count(a => a.Status is GoogleAccountStatus.Disconnected),
            workspaces.Count,
            workspaces.Sum(w => w.StorageQuotaBytes),
            pressing,
            (int)CeilingPercent,
            inFlight,
            failed,
            FailureWindowHours,
            failures,
            egress,
            EgressWindowDays);
    }

    /// <summary>
    /// One entry per day of the window, with the days nothing was served on present and zero.
    ///
    /// <para>The meter returns only the days that have something on them — right for a caller
    /// summing them, wrong for one drawing them. A chart handed a sparse list draws Monday's bar
    /// where Sunday's belongs and silently re-labels every column after it, which is the one way a
    /// chart can be wrong that a reader cannot see. Filling here rather than in the view keeps the
    /// arithmetic somewhere a test can reach it.</para>
    ///
    /// <para>A dictionary rather than a scan per day: thirty days over thirty rows is nothing either
    /// way, but the shape stops being quadratic if the window is ever widened.</para>
    /// </summary>
    private static List<UsageDay> FillTheQuietDays(
        IReadOnlyList<UsageDay> served,
        DateOnly from,
        DateOnly to)
    {
        var byDay = served.ToDictionary(d => d.Day);
        var days = new List<UsageDay>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            days.Add(byDay.TryGetValue(day, out var row) ? row : new UsageDay(day, 0, 0));
        }

        return days;
    }

    /// <summary>
    /// How full a workspace is, with a cap of zero reading as full rather than as a division by
    /// zero. The same rule <c>TenantPlanView.StoragePercent</c> already applies to the customer's own
    /// card, so a workspace on this list and that customer's own screen agree.
    /// </summary>
    private static double Percent(WorkspacePressure workspace) =>
        workspace.StorageQuotaBytes <= 0
            ? 100d
            : Math.Clamp(workspace.StorageUsedBytes * 100d / workspace.StorageQuotaBytes, 0d, 100d);
}
