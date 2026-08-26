using System.Globalization;
using System.Security.Cryptography;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.S3;

/// <summary>
/// Multipart uploads: staged to <see cref="S3StagingDirectory"/>, assembled into one Drive session.
/// </summary>
public sealed class S3MultipartStore(
    DriveUnionDbContext db,
    S3StagingDirectory staging,
    IUploadCoordinator uploads,
    TimeProvider clock,
    ILogger<S3MultipartStore> logger) : IS3Multipart
{
    public bool IsAvailable => staging.IsConfigured;

    public async Task<S3MultipartResult> BeginAsync(
        Guid tenantId,
        Guid ownerUserId,
        string key,
        string name,
        Guid? folderId,
        string mimeType,
        CancellationToken cancellationToken)
    {
        if (!staging.IsConfigured) return new S3MultipartResult(S3MultipartOutcome.NoRoom);

        var upload = new S3MultipartUpload
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Key = key,
            Name = name,
            FolderId = folderId,
            MimeType = mimeType,
            CreatedAt = clock.GetUtcNow(),
        };

        db.S3MultipartUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);

        staging.EnsureDirectory(upload.Id);

        return new S3MultipartResult(S3MultipartOutcome.Done, upload.Id);
    }

    public async Task<S3MultipartResult> StagePartAsync(
        Guid tenantId,
        Guid uploadId,
        int partNumber,
        Stream body,
        CancellationToken cancellationToken)
    {
        if (partNumber is < 1 or > S3MultipartUpload.MaxParts)
        {
            return new S3MultipartResult(S3MultipartOutcome.InvalidPartNumber);
        }

        if (!await OwnsAsync(tenantId, uploadId, cancellationToken))
        {
            return new S3MultipartResult(S3MultipartOutcome.NotFound);
        }

        // Checked before a byte is written rather than after: a volume that fills mid-part leaves a
        // truncated file that looks like a complete one, and the completion would assemble it.
        if (!staging.HasRoomFor(0)) return new S3MultipartResult(S3MultipartOutcome.NoRoom);

        staging.EnsureDirectory(uploadId);

        var path = staging.PathFor(uploadId, partNumber);

        // Written to a neighbour and moved into place, so a part that fails halfway is never a part
        // the completion can find. A client retrying that part number overwrites cleanly.
        var temporary = path + ".writing";
        long written = 0;

        // Copied by hand rather than with CopyToAsync, so the bytes can be hashed on the way past.
        // The alternative — writing the part and then reading it back to hash it — is a second pass
        // over data that was in hand once already, on the hot path of a large upload.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[64 * 1024];

        await using (var file = File.Create(temporary))
        {
            int read;

            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                digest.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }
        }

        File.Move(temporary, path, overwrite: true);

        var etag = Convert.ToHexStringLower(digest.GetHashAndReset());
        var now = clock.GetUtcNow();

        var existing = await db.S3UploadParts
            .FirstOrDefaultAsync(p => p.UploadId == uploadId && p.PartNumber == partNumber, cancellationToken);

        if (existing is null)
        {
            db.S3UploadParts.Add(new S3UploadPart
            {
                UploadId = uploadId,
                PartNumber = partNumber,
                SizeBytes = written,
                ETag = etag,
                UploadedAt = now,
            });
        }
        else
        {
            existing.SizeBytes = written;
            existing.ETag = etag;
            existing.UploadedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new S3MultipartResult(S3MultipartOutcome.Done, uploadId, etag);
    }

    public async Task<IReadOnlyList<S3PartSummary>> PartsAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        if (!await OwnsAsync(tenantId, uploadId, cancellationToken)) return [];

        return await db.S3UploadParts
            .AsNoTracking()
            .Where(p => p.UploadId == uploadId)
            .OrderBy(p => p.PartNumber)
            .Select(p => new S3PartSummary(p.PartNumber, p.SizeBytes, p.ETag, p.UploadedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<S3MultipartResult> CompleteAsync(
        Guid tenantId,
        Guid uploadId,
        IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0) return new S3MultipartResult(S3MultipartOutcome.EmptyCompletion);

        var upload = await db.S3MultipartUploads
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.TenantId == tenantId, cancellationToken);

        if (upload is null) return new S3MultipartResult(S3MultipartOutcome.NotFound);

        var staged = await db.S3UploadParts
            .AsNoTracking()
            .Where(p => p.UploadId == uploadId)
            .ToDictionaryAsync(p => p.PartNumber, cancellationToken);

        long total = 0;

        foreach (var (number, etag) in parts)
        {
            if (!staged.TryGetValue(number, out var part))
            {
                return new S3MultipartResult(S3MultipartOutcome.InvalidPart);
            }

            // The ETag is compared with the quotes stripped, because clients send it back exactly as
            // it was given — quoted — and some strip them. Both are the same answer.
            if (!string.Equals(part.ETag, etag.Trim('"'), StringComparison.OrdinalIgnoreCase))
            {
                return new S3MultipartResult(S3MultipartOutcome.InvalidPart);
            }

            if (!File.Exists(staging.PathFor(uploadId, number)))
            {
                // The row says the part is there and the disk disagrees — a sweep that ran early, or
                // a volume that was cleaned. Refusing is the only honest answer; assembling around
                // the gap would produce a file that is the wrong size and looks fine.
                return new S3MultipartResult(S3MultipartOutcome.InvalidPart);
            }

            total += part.SizeBytes;
        }

        var begun = await uploads.BeginAsync(
            tenantId,
            upload.OwnerUserId,
            new BeginUploadRequest(upload.Name, upload.MimeType, total),
            cancellationToken);

        // One Drive session, fed the parts in the order the client named them. Streamed rather than
        // concatenated first: the assembled object is never a third copy on disk.
        await using (var assembled = new PartSequenceStream(
            parts.Select(p => staging.PathFor(uploadId, p.PartNumber))))
        {
            var progress = await uploads.WriteChunkAsync(
                tenantId, begun.SessionId, assembled, 0, total, cancellationToken);

            if (progress.StoredFileId is not { } storedId)
            {
                // The staged parts are left alone. A completion that failed at Drive is one the
                // client can retry, and throwing the bytes away would make that retry a re-upload.
                logger.LogWarning(
                    "Assembling S3 upload {UploadId} failed: {Reason}", uploadId, progress.FailureReason);

                return new S3MultipartResult(S3MultipartOutcome.NotFound);
            }

            if (upload.FolderId is { } folderId)
            {
                await db.StoredFiles
                    .Where(f => f.Id == storedId && f.TenantId == tenantId)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.FolderId, folderId), cancellationToken);
            }

            await DiscardAsync(upload, cancellationToken);

            // S3's multipart ETag is «hash-of-the-part-hashes» with the count after a dash. Clients
            // that verify it compare against their own computation of the same, so it is built the
            // same way rather than invented.
            var etag = MultipartEtag(parts.Select(p => staged[p.PartNumber].ETag));

            return new S3MultipartResult(S3MultipartOutcome.Done, uploadId, etag, storedId);
        }
    }

    public async Task<S3MultipartResult> AbortAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var upload = await db.S3MultipartUploads
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.TenantId == tenantId, cancellationToken);

        if (upload is null) return new S3MultipartResult(S3MultipartOutcome.NotFound);

        await DiscardAsync(upload, cancellationToken);

        return new S3MultipartResult(S3MultipartOutcome.Done);
    }

    public async Task<int> SweepAbandonedAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - S3MultipartUpload.Abandoned;

        // Read then filter, because this compares a DateTimeOffset and SQLite will not do that in
        // SQL — the wall this codebase keeps meeting. There are never many rows here: an upload in
        // flight or an upload somebody abandoned, and the second kind is what this is removing.
        var candidates = await db.S3MultipartUploads
            .AsNoTracking()
            .Select(u => new { u.Id, u.CreatedAt })
            .ToListAsync(cancellationToken);

        var stale = candidates.Where(u => u.CreatedAt < cutoff).Select(u => u.Id).ToList();

        foreach (var id in stale)
        {
            staging.Discard(id);

            await db.S3UploadParts.Where(p => p.UploadId == id).ExecuteDeleteAsync(cancellationToken);
            await db.S3MultipartUploads.Where(u => u.Id == id).ExecuteDeleteAsync(cancellationToken);
        }

        if (stale.Count > 0)
        {
            logger.LogInformation("Swept {Count} abandoned S3 multipart uploads.", stale.Count);
        }

        return stale.Count;
    }

    private async Task DiscardAsync(S3MultipartUpload upload, CancellationToken cancellationToken)
    {
        staging.Discard(upload.Id);

        await db.S3UploadParts.Where(p => p.UploadId == upload.Id).ExecuteDeleteAsync(cancellationToken);
        await db.S3MultipartUploads.Where(u => u.Id == upload.Id).ExecuteDeleteAsync(cancellationToken);
    }

    private Task<bool> OwnsAsync(Guid tenantId, Guid uploadId, CancellationToken cancellationToken) =>
        db.S3MultipartUploads.AnyAsync(u => u.Id == uploadId && u.TenantId == tenantId, cancellationToken);

    /// <summary>S3's own multipart ETag: MD5 of the concatenated part MD5s, then «-{count}».</summary>
    private static string MultipartEtag(IEnumerable<string> partEtags)
    {
        var list = partEtags.ToList();
        var concatenated = list.SelectMany(Convert.FromHexString).ToArray();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Convert.ToHexStringLower(MD5.HashData(concatenated))}-{list.Count}");
    }
}

/// <summary>
/// Several files read end to end as one forward-only stream.
///
/// <para>What lets a completion hand the upload coordinator one body without concatenating the parts
/// into a third copy on disk first. Forward-only and unseekable on purpose — the coordinator streams
/// it to Drive and never looks back, and a seekable one would be a promise this cannot keep across a
/// file boundary.</para>
/// </summary>
internal sealed class PartSequenceStream(IEnumerable<string> paths) : Stream
{
    private readonly IEnumerator<string> _paths = paths.GetEnumerator();
    private FileStream? _current;
    private bool _finished;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (!_finished)
        {
            if (_current is null)
            {
                if (!_paths.MoveNext())
                {
                    _finished = true;
                    return 0;
                }

                _current = File.OpenRead(_paths.Current);
            }

            var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read > 0) return read;

            // A part ended. The next one continues the same object, so this loops rather than
            // returning zero — a zero here would tell the coordinator the body was over.
            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _paths.Dispose();
        }

        base.Dispose(disposing);
    }
}
