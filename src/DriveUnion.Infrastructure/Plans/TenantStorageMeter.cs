using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// <b>The only writer of <c>Tenant.StorageUsedBytes</c>.</b> Reserve, settle, release, and nothing
/// else touches the counter.
///
/// <para>This is M5 §7's mechanism and P1 builds the minimum of it the per-file limit needs: the
/// conditional reserve before Google is contacted, the settle on the chunk that finishes the file,
/// and the release on failure. What it deliberately does <b>not</b> build is the release on
/// deletion — that one has to happen when Drive <i>confirms</i> the bytes are gone, not when the
/// customer clicks delete, and the delete path is another slice's. Releasing optimistically means
/// the tenant's counter says free while the operator's pool says full, and the operator eats the
/// difference silently.</para>
///
/// <para>Static, and taking the context per call, for two reasons: it must be reachable from
/// <c>UploadCoordinator</c> without changing that class's constructor, which several test harnesses
/// already build by hand; and "one writer" is easier to believe about a type nothing can be
/// registered as a substitute for.</para>
/// </summary>
public static class TenantStorageMeter
{
    /// <summary>
    /// Takes <paramref name="bytes"/> of the tenant's remaining room, and says whether there was
    /// room to take.
    ///
    /// <para>One conditional UPDATE: the test and the increment are the same statement, so the
    /// database decides. Check-then-act in C# loses this race — ten parallel 60 GB uploads into a
    /// 500 GB cap each read "there is room" and land at 600 GB, which is a bug that only appears
    /// under a real user with a real connection.</para>
    ///
    /// <para>Rows affected is the answer. False means no room, and the caller must not proceed to
    /// Google: refusing after the resumable session is open orphans a session on Google's side, and
    /// the client deserves to learn "no" from the round trip that costs one request.</para>
    ///
    /// <para>Every true must be finished by exactly one <see cref="SettleAsync"/> when the upload
    /// completed, or one <see cref="ReleaseAsync"/> when it did not.</para>
    /// </summary>
    public static async Task<bool> TryReserveAsync(
        DriveUnionDbContext db,
        Guid tenantId,
        long bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        var affected = await db.Tenants
            .Where(t => t.Id == tenantId && t.StorageUsedBytes + bytes <= t.StorageQuotaBytes)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.StorageUsedBytes, t => t.StorageUsedBytes + bytes),
                cancellationToken);

        if (affected == 0) return false;

        DetachStaleCounter(db, tenantId);

        return true;
    }

    /// <summary>
    /// Replaces a reservation with what the file actually turned out to be.
    ///
    /// <para>Drive reports the stored size on the response that completes the upload, and it is the
    /// only figure that is evidence. A file that came in smaller than declared gives the difference
    /// back; one that came in larger keeps it, because the bytes are genuinely there — M5 §7 lets a
    /// session that already reserved finish even when that carries the tenant past its cap, since
    /// killing a 90%-complete 200 GB upload is a worse outcome than a temporary overage. The cap
    /// blocks the next upload instead.</para>
    /// </summary>
    public static async Task SettleAsync(
        DriveUnionDbContext db,
        Guid tenantId,
        long reservedBytes,
        long actualBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var difference = actualBytes - reservedBytes;

        if (difference == 0) return;

        if (difference > 0)
        {
            await db.Tenants
                .Where(t => t.Id == tenantId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.StorageUsedBytes, t => t.StorageUsedBytes + difference),
                    cancellationToken);

            DetachStaleCounter(db, tenantId);
            return;
        }

        await ReleaseAsync(db, tenantId, -difference, cancellationToken);
    }

    /// <summary>
    /// Gives a reservation back, for bytes that never landed: the pool refused, Drive would not open
    /// a session, the session expired, or the client abandoned it.
    ///
    /// <para>The floor lives in the WHERE rather than in the new value, so it is applied by the same
    /// statement that does the subtraction. A release that computed <c>Math.Max(0, used - n)</c> from
    /// a value it read would still write a negative when two of them overlap, and a negative counter
    /// is free storage for as long as it takes somebody to notice.</para>
    ///
    /// <para>The consequence of the floor is that a release against a counter that no longer holds
    /// the bytes does nothing at all, leaving the tenant's usage reading <i>higher</i> than the truth.
    /// That is the right direction to be wrong in for a number that refuses service: it costs the
    /// customer an upload and a support message, where the other direction costs the operator a pool.
    /// M5 §7's reconciliation is what finds it.</para>
    /// </summary>
    public static async Task ReleaseAsync(
        DriveUnionDbContext db,
        Guid tenantId,
        long bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        if (bytes == 0) return;

        var affected = await db.Tenants
            .Where(t => t.Id == tenantId && t.StorageUsedBytes >= bytes)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.StorageUsedBytes, t => t.StorageUsedBytes - bytes),
                cancellationToken);

        if (affected > 0) DetachStaleCounter(db, tenantId);
    }

    /// <summary>What the meter currently says, for the body of a refusal. Zeroes for an unknown tenant.</summary>
    public static async Task<(long UsedBytes, long QuotaBytes)> ReadAsync(
        DriveUnionDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.StorageUsedBytes, t.StorageQuotaBytes })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? (0, 0) : (row.StorageUsedBytes, row.StorageQuotaBytes);
    }

    /// <summary>
    /// <c>ExecuteUpdate</c> goes round the change tracker, so a copy someone else loaded still holds
    /// the old counter. Left attached, the next <c>SaveChanges</c> in this scope — the upload session
    /// row this very reservation was taken for — would write that stale number back over the move.
    /// </summary>
    private static void DetachStaleCounter(DriveUnionDbContext db, Guid tenantId)
    {
        var stale = db.ChangeTracker.Entries<Tenant>()
            .FirstOrDefault(e => e.Entity.Id == tenantId);

        if (stale is not null) stale.State = EntityState.Detached;
    }
}
