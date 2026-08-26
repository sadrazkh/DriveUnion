using DriveUnion.Core.Application;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads what a browser needs to open an encrypted file. Writes are the upload coordinator's, which
/// is the only place a header can arrive from.
/// </summary>
public sealed class FileEncryptionStore(DriveUnionDbContext db) : IFileEncryption
{
    public async Task<EncryptionHeader?> ForFileAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken) =>
        await db.FileEncryptions
            .AsNoTracking()

            // Both predicates, though the id alone would find the row: the tenant is on this table
            // precisely so that reading a header cannot be a way to reach another workspace's file
            // through a guessed id.
            .Where(e => e.StoredFileId == fileId && e.TenantId == tenantId)
            .Select(e => new EncryptionHeader(
                e.Scheme,
                e.SegmentSize,
                e.NoncePrefix,
                e.PlaintextLength,
                e.KdfSalt,
                e.KdfIterations,
                e.WrappedKey))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> EncryptedAmongAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new HashSet<Guid>();

        // Ids only. A listing draws a padlock, and sending every wrapped key to a screen that needed
        // one bit per row would be handing out material for nothing.
        var encrypted = await db.FileEncryptions
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && fileIds.Contains(e.StoredFileId))
            .Select(e => e.StoredFileId)
            .ToListAsync(cancellationToken);

        return encrypted.ToHashSet();
    }
}
