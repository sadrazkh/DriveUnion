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

    public async Task<IReadOnlyDictionary<Guid, long>> PlaintextLengthsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new Dictionary<Guid, long>();

        // Two columns. A listing draws a padlock and a size; the wrapped key that would open the file
        // has no business on a screen that needed neither.
        var rows = await db.FileEncryptions
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && fileIds.Contains(e.StoredFileId))
            .Select(e => new { e.StoredFileId, e.PlaintextLength })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.StoredFileId, r => r.PlaintextLength);
    }
}
