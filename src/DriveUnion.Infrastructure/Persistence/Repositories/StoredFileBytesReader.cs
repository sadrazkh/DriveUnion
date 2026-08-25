using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// <see cref="IStoredFileBytes"/> over the catalogue's own rows.
/// </summary>
public sealed class StoredFileBytesReader(DriveUnionDbContext db) : IStoredFileBytes
{
    public async Task<StoredFileBytes?> ResolveAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken) =>
        await db.StoredFiles
            .AsNoTracking()

            // Live only. A file in the trash is still in the operator's Drive and still occupies the
            // customer's quota, and it is still deleted: an API that streamed it would be a way to
            // read something the panel says is gone.
            .Where(f => f.Id == fileId && f.TenantId == tenantId && f.DeletedAt == null)
            .Select(f => new StoredFileBytes(f.GoogleAccountId, f.DriveFileId, f.MimeType, f.SizeBytes))
            .FirstOrDefaultAsync(cancellationToken);
}
