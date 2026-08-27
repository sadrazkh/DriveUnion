using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Dashboard;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// The two dashboard readers over <see cref="ServiceTestHarness"/>'s real SQLite database.
///
/// <para>SQLite rather than EF's in-memory provider, and here it is doing more than realism. These
/// readers are shaped by what SQLite <i>refuses</i> — it keeps a <c>DateTimeOffset</c> as text and
/// will neither compare nor <c>ORDER BY</c> one — so every date decision in them is made in memory
/// on purpose. The in-memory provider evaluates everything client-side and would happily run the
/// queries this provider rejects, which is exactly the failure it would need to catch.</para>
///
/// <para>The readers are built from their real dependencies rather than from stubs. The customer's
/// is handed the same <c>TenantPlanService</c> and <c>TrashService</c> the sidebar's capacity card
/// reads, because «the dashboard and the capacity card cannot disagree about how full a workspace
/// is» is the property being kept, and a stub would remove exactly it.</para>
/// </summary>
internal static class DashboardReaderSupport
{
    public static CustomerDashboardReader CustomerDashboard(
        this ServiceTestHarness harness,
        DriveUnionDbContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var db = context ?? harness.Db;

        return new CustomerDashboardReader(
            db,
            harness.PlanService(context: db),
            new TrashService(db, harness.Drive, NullLogger<TrashService>.Instance),
            new TrafficMeter(db, harness.Clock, NullLogger<TrafficMeter>.Instance),
            harness.Clock);
    }

    public static OperatorDashboardReader OperatorDashboard(
        this ServiceTestHarness harness,
        DriveUnionDbContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var db = context ?? harness.Db;

        // The real TrafficMeter, like the customer's reader gets. The egress chart is one of the two
        // things this reader is now about, and a stub handing back canned days would be testing the
        // chart's arithmetic against itself rather than against the rows the product writes.
        return new OperatorDashboardReader(
            db,
            new PoolListing(db),
            new TrafficMeter(db, harness.Clock, NullLogger<TrafficMeter>.Instance),
            harness.Clock);
    }

    public static UploadSession SeedSession(
        this ServiceTestHarness harness,
        Guid tenantId,
        Guid accountId,
        string fileName,
        UploadSessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string? failureReason = null)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
            FileName = fileName,
            MimeType = "application/octet-stream",
            SizeBytes = 1024,
            DriveResumableUri = "https://upload.invalid/session",
            Status = status,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            FailureReason = failureReason,
        };

        harness.Db.UploadSessions.Add(session);
        harness.Db.SaveChanges();

        return session;
    }

    /// <summary>
    /// <see cref="IGoogleAccountDirectory"/> narrowed to the one method the operator's dashboard
    /// calls, reading the real table.
    ///
    /// <para>It is not a stub handing back canned rows, and that is the point: what these tests are
    /// about is that the pool's totals come from the accounts that are actually there with a
    /// disconnected one left out, and a fake list would be testing the arithmetic against itself.
    /// The real directory is not used only because its constructor pulls in a token service, an
    /// about-reader and a protector that no dashboard read ever touches.</para>
    /// </summary>
    private sealed class PoolListing(DriveUnionDbContext db) : IGoogleAccountDirectory
    {
        public async Task<IReadOnlyList<GoogleAccountSummary>> ListAsync(
            CancellationToken cancellationToken)
        {
            var accounts = await db.GoogleAccounts
                .AsNoTracking()
                .Select(a => new GoogleAccountSummary(
                    a.Id,
                    a.Email,
                    a.Label,
                    a.Status,
                    a.QuotaTotalBytes,
                    a.QuotaUsedBytes,
                    a.CreatedAt))
                .ToListAsync(cancellationToken);

            return [.. accounts.OrderBy(a => a.CreatedAt)];
        }

        public Task<Guid> ConnectAsync(
            string authorizationCode,
            string redirectUri,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A dashboard reads the pool; it never connects to it.");

        public Task<bool> DisconnectAsync(Guid accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A dashboard reads the pool; it never changes it.");

        public Task RefreshQuotaAsync(Guid accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A dashboard reads the pool; it never asks Google.");
    }
}
