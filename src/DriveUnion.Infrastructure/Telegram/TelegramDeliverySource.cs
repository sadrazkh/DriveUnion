using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// One tenant-scoped read, and the only place in this slice that learns which account holds a file.
///
/// <para>The tenant is part of the lookup rather than a check after it, and the answer for "not
/// yours", "deleted" and "never existed" is the same null. That is the same rule the public download
/// path follows and the same one the file card follows: a distinguishable "not yours" is what turns
/// an id into something worth guessing, and every id that reaches this method came out of a
/// <c>callback_data</c> string a client we do not control sent us.</para>
/// </summary>
public sealed class TelegramDeliverySource(DriveUnionDbContext db) : ITelegramDeliverySource
{
    public async Task<TelegramDeliveryTicket?> ResolveAsync(
        Guid tenantId,
        Guid storedFileId,
        CancellationToken cancellationToken)
    {
        return await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.Id == storedFileId && f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new TelegramDeliveryTicket(
                f.Id,
                f.GoogleAccountId,
                f.DriveFileId,
                f.Name,
                f.MimeType,
                f.SizeBytes,
                db.FileEncryptions.Any(e => e.StoredFileId == f.Id)))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
