using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// M1's answer to "which account gets this file": the one that is connected and has room.
///
/// There is exactly one account in M1, so the interesting half of this class is the refusal. M2
/// replaces the ordering with a real policy — most free space, round robin, manual priority — and
/// nothing above it changes.
/// </summary>
public sealed class SingleAccountUploadTargetSelector(DriveUnionDbContext db) : IUploadTargetSelector
{
    public async Task<Guid?> SelectAsync(long sizeBytes, CancellationToken cancellationToken)
    {
        if (sizeBytes < 0) return null;

        var candidates = await db.GoogleAccounts
            .AsNoTracking()
            .Where(a => a.Status == GoogleAccountStatus.Healthy)
            .Select(a => new { a.Id, a.QuotaTotalBytes, a.QuotaUsedBytes })
            .ToListAsync(cancellationToken);

        return candidates
            // A quota of zero means nobody has asked Google yet, not that the account is full.
            // Treating "unknown" as "no room" would refuse every upload until the first quota
            // refresh, which is a dead product that looks like a storage problem.
            .Where(a => a.QuotaTotalBytes <= 0 || a.QuotaTotalBytes - a.QuotaUsedBytes >= sizeBytes)
            .OrderByDescending(a => a.QuotaTotalBytes - a.QuotaUsedBytes)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefault();
    }
}
