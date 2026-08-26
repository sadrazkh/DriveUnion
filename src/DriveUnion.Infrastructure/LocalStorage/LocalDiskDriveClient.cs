using System.Buffers;
using System.Collections.Concurrent;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Uploads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// Google Drive, standing on one box's disk.
///
/// The product cannot be seen working without a Drive account, and a Drive account needs a Google
/// Cloud project the owner does not have yet. Everything the product sells — upload a file, get a
/// link, open <c>/d/{slug}</c>, seek in the video, resume the download — funnels through
/// <see cref="IDriveClient"/>, so a second implementation of it is the whole demo.
///
/// It is not a test double. A test double may hold a file in a <c>MemoryStream</c> and forget it when
/// the process ends; this one is a running backend, so the bytes and the open upload sessions are on
/// disk and survive a restart, and the download hands back a file handle rather than a copy.
///
/// What it deliberately copies from <c>GoogleDriveClient</c>, because the callers above it are
/// written against these and nothing else:
/// <list type="bullet">
/// <item>a resumable session acknowledges <b>one contiguous prefix anchored at zero</b> — a chunk that
/// does not continue it is refused rather than stored somewhere clever;</item>
/// <item>the confirmed length is what is on disk, not what a client believes it sent;</item>
/// <item>a session that is gone or past its week is
/// <see cref="DriveUploadSessionExpiredException"/>, not a slow failure per chunk;</item>
/// <item>a satisfiable <c>Range</c> is a 206 with a <c>Content-Range</c>, even when the slice is the
/// whole file.</item>
/// </list>
///
/// It must never run in production: these bytes are on one disk, with no replication and no backup,
/// and a file host that quietly kept customers' files there instead of in the account pool is not a
/// bug anybody notices until the disk dies. <see cref="LocalDiskDriveServiceCollectionExtensions"/>
/// keeps it off by default and refuses to start the host in <c>Production</c>.
/// </summary>
public sealed class LocalDiskDriveClient : IDriveClient
{
    /// <summary>Matches <see cref="Stream.CopyToAsync(Stream)"/>'s own; large enough to keep the disk busy.</summary>
    private const int CopyBufferSize = 80 * 1024;

    private const string FallbackMimeType = "application/octet-stream";

    private readonly LocalDiskDriveOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalDiskDriveClient> _logger;
    private readonly LocalDiskStore _store;

    /// <summary>
    /// One gate per session, held across the byte copy. Drive acknowledges a single contiguous
    /// prefix, so two writers in one session are already unsupported; serialising them here makes
    /// "check the offset, write the bytes, record the new length" one indivisible step rather than
    /// three a second request can interleave with.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();

    /// <summary>
    /// One gate for every account's folder index. Folders are created twice per upload and the index
    /// is a read-modify-write of a small file; a lock per account would buy nothing but a way to
    /// leak one per account id ever seen.
    /// </summary>
    private readonly SemaphoreSlim _folderGate = new(1, 1);

    public LocalDiskDriveClient(
        IOptions<LocalDiskDriveOptions> options,
        TimeProvider clock,
        ILogger<LocalDiskDriveClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _clock = clock;
        _logger = logger;

        // Constructing this while it is switched off can only be a mistake, and it is one that ends
        // with customer files on a box instead of in Drive. It is cheaper to refuse here than to
        // explain later where the files went.
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                $"The local-disk Drive backend was constructed while {LocalDiskDriveOptions.SectionName}:Enabled "
                + "is false. It is a development substitute for Google Drive and it does not run unless "
                + "a deployment asks for it by name.");
        }

        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException(
                $"{LocalDiskDriveOptions.SectionName}:RootPath is not configured. The local-disk backend "
                + "has nowhere to put files, and it will not pick a directory on its own.");
        }

        RootPath = Path.GetFullPath(_options.RootPath);

        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(LocalDiskLayout.SessionsDirectory(RootPath));

        _store = new LocalDiskStore(RootPath);
    }

    /// <summary>The resolved storage root. Absolute, and created by the time this exists.</summary>
    public string RootPath { get; }

    public async Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SizeBytes < 0)
        {
            throw new DriveApiException(
                $"An upload of {request.SizeBytes} bytes is not a file. Drive rejects the size at "
                + "initiation and so does this.");
        }

        var now = _clock.GetUtcNow();
        var session = new LocalUploadSessionRecord
        {
            SessionId = Guid.NewGuid(),

            // The identifier the bytes live under is chosen now rather than at completion, so chunks
            // stream straight into their final location and nothing has to be moved at the end. Drive
            // only reveals its id with the last response, but that is a fact about its API — no
            // caller can observe the difference, because the id is returned in the same place.
            FileId = Guid.NewGuid(),
            AccountId = accountId,
            FileName = request.FileName,
            MimeType = string.IsNullOrWhiteSpace(request.MimeType) ? FallbackMimeType : request.MimeType,
            SizeBytes = request.SizeBytes,
            ParentFolderId = request.ParentFolderId,
            CreatedAt = now,
            ExpiresAt = now + _options.SessionLifetime,
        };

        Directory.CreateDirectory(LocalDiskLayout.FilesDirectory(RootPath, accountId));
        await _store.WriteSessionAsync(session, cancellationToken).ConfigureAwait(false);

        return new DriveResumableSession(LocalDiskLayout.SessionUri(session.SessionId), session.ExpiresAt);
    }

    public async Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);
        ArgumentNullException.ThrowIfNull(content);

        if (!UploadChunking.IsValidChunk(offset, length, totalSize))
        {
            throw new DriveApiException(
                $"Refusing to write {length} bytes at offset {offset} of {totalSize}. Drive would not "
                + "reject this — it would accept the request and quietly stop acknowledging bytes.");
        }

        var sessionId = RequireSessionId(sessionUri);
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await RequireLiveSessionAsync(sessionId, sessionUri, cancellationToken)
                .ConfigureAwait(false);

            if (totalSize != session.SizeBytes)
            {
                throw new DriveApiException(
                    $"This session was opened for {session.SizeBytes} bytes; a chunk declaring a total "
                    + $"of {totalSize} belongs to some other file.");
            }

            if (session.Completed)
            {
                // Drive answers a replayed final chunk with the finished file rather than an error,
                // and the caller above treats a second completion as the same upload. Nothing is
                // read from the body: there is nowhere left to put it.
                var finished = await RequireFileAsync(session.AccountId, session.FileId, cancellationToken)
                    .ConfigureAwait(false);

                return new DriveChunkOutcome(session.SizeBytes, ToMetadata(finished));
            }

            if (offset != session.ConfirmedLength)
            {
                // One contiguous prefix, anchored at zero. A writer that jumps ahead is not slow,
                // it is wrong, and the file it would assemble has a hole in it.
                throw new DriveApiException(
                    $"Chunk offset {offset} does not continue this session, which has "
                    + $"{session.ConfirmedLength} bytes confirmed.");
            }

            var path = LocalDiskLayout.ContentPath(RootPath, session.AccountId, session.FileId);
            Directory.CreateDirectory(LocalDiskLayout.FilesDirectory(RootPath, session.AccountId));

            var isFinal = offset + length == totalSize;

            await using (var file = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous,

                    // No FileStream-level buffer: the copy below brings its own and a second one
                    // would only mean every chunk is written to memory before it is written to disk.
                    BufferSize = 0,
                }))
            {
                // Seek rather than append. A chunk that died halfway through last time left bytes
                // past the confirmed prefix, and those bytes are not evidence of anything — they get
                // overwritten by the retry that lands here.
                file.Position = offset;

                await CopyChunkAsync(content, file, length, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);

                // The tail of an interrupted earlier attempt can sit past the end of the file. The
                // last chunk is where that is cut off, because from here the length is the file's.
                if (isFinal) file.SetLength(totalSize);
            }

            session.ConfirmedLength = offset + length;

            DriveFileMetadata? completed = null;
            if (isFinal)
            {
                var record = new LocalFileRecord
                {
                    FileId = session.FileId,
                    Name = session.FileName,
                    MimeType = session.MimeType,
                    SizeBytes = totalSize,
                    CreatedAt = session.CreatedAt,
                    ModifiedAt = _clock.GetUtcNow(),
                    ParentFolderId = session.ParentFolderId,
                };

                // Written before the session record: metadata is what makes the bytes a file, and a
                // crash between the two leaves a finished file whose session still asks for its last
                // chunk — which the client resends over identical bytes. The other order leaves a
                // completed session pointing at a file that does not exist.
                await _store.WriteFileAsync(session.AccountId, record, cancellationToken)
                    .ConfigureAwait(false);

                session.Completed = true;
                completed = ToMetadata(record);
            }

            // The commit. Everything it counts is already on the disk, which is what makes the
            // acknowledged prefix a fact rather than an intention.
            await _store.WriteSessionAsync(session, cancellationToken).ConfigureAwait(false);

            // Nothing will write into a finished session again, and dropping its gate keeps the
            // dictionary the size of the uploads in flight rather than of every upload the process
            // has ever seen.
            if (session.Completed) _sessionGates.TryRemove(sessionId, out _);

            return new DriveChunkOutcome(session.ConfirmedLength, completed);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);

        var sessionId = RequireSessionId(sessionUri);
        var session = await RequireLiveSessionAsync(sessionId, sessionUri, cancellationToken)
            .ConfigureAwait(false);

        // A finished upload answers with the whole file, the way Drive answers a probe with 200 when
        // the client and our row simply had not caught up.
        return session.Completed ? session.SizeBytes : session.ConfirmedLength;
    }

    public async Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveFileId);

        if (!LocalDiskLayout.TryParseFileId(driveFileId, out var fileId))
        {
            // Not one of ours, which for a caller is indistinguishable from a file that is not there
            // — and, more to the point, means no part of this string ever became a path.
            throw new DriveApiException($"There is no file {driveFileId} on this disk.");
        }

        var record = await RequireFileAsync(accountId, fileId, cancellationToken).ConfigureAwait(false);
        var path = LocalDiskLayout.ContentPath(RootPath, accountId, fileId);

        if (!File.Exists(path))
        {
            throw new DriveApiException(
                $"File {driveFileId} is recorded in account {accountId} but its bytes are missing from "
                + "the local-disk store.");
        }

        var file = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,

                // Unbuffered. The caller copies this straight to the wire with its own buffer; a
                // second one here would double the memory a concurrent download costs and buy nothing.
                BufferSize = 0,
            });

        try
        {
            // The file's own length, not the recorded size. What is on the disk is what can be
            // served, and a range answered against a number the bytes disagree with is a truncated
            // download that reports itself as complete.
            var total = file.Length;
            var contentType = string.IsNullOrWhiteSpace(record.MimeType) ? FallbackMimeType : record.MimeType;

            if (ByteRangeParser.Resolve(rangeHeader, total) is not { } range)
            {
                return new DriveDownload(file, contentType, total, null, false, new FileHandleOwner(file));
            }

            // A range covering the whole file is still a 206 — but it needs no window, because there
            // is nothing to clamp. `Range: bytes=0-` is most of the requests a browser makes.
            var content = range.Start == 0 && range.Length == total
                ? file
                : (Stream)new FileWindowStream(file, range.Start, range.Length);

            return new DriveDownload(
                content,
                contentType,
                range.Length,
                $"bytes {range.Start}-{range.End}/{total}",
                isPartial: true,
                new FileHandleOwner(file));
        }
        catch
        {
            // An unsatisfiable range throws from inside here, and a handle left open by a request
            // that produced no download is a file nothing will ever close.
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A move here is a label change and nothing else.
    ///
    /// <para>Bytes on this backend live at <c>ContentPath(root, account, fileId)</c>, which no folder
    /// appears in — so the trash is a folder id written on a record, not a directory anything is
    /// carried into. That is the same shape Drive has, where a parent is metadata rather than a
    /// location, and it is why the trash needed no second code path for this backend.</para>
    /// </summary>
    public async Task MoveAsync(
        Guid accountId,
        string driveFileId,
        string? fromFolderId,
        string toFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveFileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toFolderId);

        if (!LocalDiskLayout.TryParseFileId(driveFileId, out var fileId))
        {
            throw new DriveApiException($"There is no file {driveFileId} on this disk.");
        }

        var record = await RequireFileAsync(accountId, fileId, cancellationToken).ConfigureAwait(false);

        record.ParentFolderId = toFolderId;
        await _store.WriteFileAsync(accountId, record, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid accountId, string driveFileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveFileId);

        if (!LocalDiskLayout.TryParseFileId(driveFileId, out var fileId))
        {
            // Already not here, in the only sense this backend has. The purge wants the row and the
            // bytes to agree, and they do.
            return;
        }

        // Content first, metadata second. A record without bytes reads as corruption and is caught
        // loudly by OpenDownloadAsync; bytes without a record are invisible and are never freed.
        var content = LocalDiskLayout.ContentPath(RootPath, accountId, fileId);
        var metadata = LocalDiskLayout.MetadataPath(RootPath, accountId, fileId);

        if (File.Exists(content)) File.Delete(content);
        if (File.Exists(metadata)) File.Delete(metadata);

        _logger.LogInformation(
            "Permanently deleted local-disk file {DriveFileId} from account {AccountId}.",
            driveFileId,
            accountId);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        await _folderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await _store.ReadFoldersAsync(accountId, cancellationToken).ConfigureAwait(false);

            var existing = index.Folders.Find(
                f => string.Equals(f.Name, folderName, StringComparison.Ordinal)
                    && string.Equals(f.ParentFolderId, parentFolderId, StringComparison.Ordinal));

            if (existing is not null) return existing.Id;

            // The parent is recorded, not resolved. No byte's location depends on a folder here, so
            // an id left over from a Google-backed run against the same database is a label this
            // backend can keep rather than a reference it has to reject.
            var folder = new LocalFolderRecord
            {
                Id = LocalDiskLayout.NewFolderId(),
                Name = folderName,
                ParentFolderId = parentFolderId,
            };

            index.Folders.Add(folder);
            await _store.WriteFoldersAsync(accountId, index, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Created local-disk folder {FolderName} ({FolderId}) in account {AccountId}.",
                folderName,
                folder.Id,
                accountId);

            return folder.Id;
        }
        finally
        {
            _folderGate.Release();
        }
    }

    /// <summary>
    /// What the volume says, rather than a number invented to look like Google One.
    ///
    /// The account id is ignored, because there is only one disk: every account this backend serves
    /// shares it, and the honest answer to "does this file fit" is whether the volume has room. That
    /// is exactly how the number is used — <c>SingleAccountUploadTargetSelector</c> subtracts usage
    /// from the limit and compares it against the upload — so the pair reported here is the volume's
    /// total and the volume's used space, including everything on it that is not this product's.
    /// </summary>
    /// <summary>
    /// What is on the disk for this file, with a checksum computed from the bytes themselves.
    ///
    /// <para>Drive publishes an md5Checksum it maintains; this backend has no such record, so it
    /// reads the file. That is the honest implementation rather than the cheap one — the caller is
    /// about to delete somebody's only other copy on the strength of this answer, and returning null
    /// there would either stop every migration on this backend or, worse, be treated as «no
    /// objection».</para>
    /// </summary>
    public async Task<DriveFileMetadata?> GetFileAsync(
        Guid accountId,
        string driveFileId,
        CancellationToken cancellationToken)
    {
        if (!LocalDiskLayout.TryParseFileId(driveFileId, out var fileId)) return null;

        var record = await _store.ReadFileAsync(accountId, fileId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;

        var content = LocalDiskLayout.ContentPath(RootPath, accountId, fileId);
        if (!File.Exists(content)) return null;

        await using var stream = File.OpenRead(content);
        var hash = await System.Security.Cryptography.MD5.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);

        return ToMetadata(record) with { Md5Checksum = Convert.ToHexStringLower(hash) };
    }

    public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            var volume = new DriveInfo(Path.GetPathRoot(RootPath) ?? RootPath);

            return Task.FromResult(
                new DriveStorageQuota(volume.TotalSize, volume.TotalSize - volume.AvailableFreeSpace));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new DriveApiException(
                $"The volume holding {RootPath} could not be measured, so there is no honest quota to "
                + "report for it.",
                ex);
        }
    }

    private static DriveFileMetadata ToMetadata(LocalFileRecord record) =>
        new(
            LocalDiskLayout.FileId(record.FileId),
            record.Name,
            record.MimeType,
            record.SizeBytes,
            record.CreatedAt,
            record.ModifiedAt);

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes, and says so loudly when the body disagrees.
    ///
    /// The bound is not politeness: without it a body that never ends fills the disk, and the caller
    /// hands over a live request stream whose length is a claim in a header. A body that is short is
    /// a chunk that died on the wire; one that is long is a client that has lost track of what it is
    /// sending, and storing its prefix silently assembles a file that is wrong from here on.
    /// </summary>
    private static async Task CopyChunkAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long copied = 0;

            while (copied < length)
            {
                var want = (int)Math.Min(buffer.Length, length - copied);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                copied += read;
            }

            if (copied != length)
            {
                throw new DriveApiException(
                    $"The chunk body carried {copied} bytes but declared {length}.");
            }

            var extra = await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (extra > 0)
            {
                throw new DriveApiException(
                    $"The chunk body carried more than the {length} bytes it declared.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// A session URI that is not one this backend issued is a session that does not exist — the same
    /// answer Drive gives, and the reason no part of a caller-supplied URI ever reaches the disk.
    /// </summary>
    private static Guid RequireSessionId(Uri sessionUri) =>
        LocalDiskLayout.TryParseSessionUri(sessionUri, out var sessionId)
            ? sessionId
            : throw new DriveUploadSessionExpiredException(
                $"There is no resumable upload session at {sessionUri.Host}; this backend did not "
                + "issue it. The upload has to be started again.");

    private async Task<LocalUploadSessionRecord> RequireLiveSessionAsync(
        Guid sessionId,
        Uri sessionUri,
        CancellationToken cancellationToken)
    {
        var session = await _store.ReadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new DriveUploadSessionExpiredException(
                $"There is no resumable upload session at {sessionUri.Host} any more. Sessions last "
                + $"{_options.SessionLifetime.TotalDays:0.#} days; this one has to be started over.");

        if (_clock.GetUtcNow() >= session.ExpiresAt)
        {
            throw new DriveUploadSessionExpiredException(
                $"This upload session expired at {session.ExpiresAt:u} and cannot be resumed. Start "
                + "the upload again.");
        }

        return session;
    }

    private async Task<LocalFileRecord> RequireFileAsync(
        Guid accountId,
        Guid fileId,
        CancellationToken cancellationToken) =>
        await _store.ReadFileAsync(accountId, fileId, cancellationToken).ConfigureAwait(false)
        ?? throw new DriveApiException(
            $"There is no file {LocalDiskLayout.FileId(fileId)} in account {accountId} on this disk.");

    /// <summary>
    /// Keeps the handle alive for exactly as long as the download taken out of it, the way the real
    /// client keeps its HTTP response. Disposing twice — once as the content stream, once as the
    /// owner — is what <see cref="DriveDownload"/> does, and a <see cref="FileStream"/> does not mind.
    /// </summary>
    private sealed class FileHandleOwner(FileStream file) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => file.DisposeAsync();
    }
}
