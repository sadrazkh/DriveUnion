using System.Globalization;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Uploads;

namespace DriveUnion.Tests.Fakes;

/// <summary>The six things <see cref="IDriveClient"/> can be asked to do.</summary>
public enum FakeDriveOperation
{
    BeginResumableUpload,
    WriteChunk,
    GetConfirmedLength,
    OpenDownload,
    EnsureFolder,
    GetStorageQuota,
    Move,
    Delete,
    GetFile,
}

/// <summary>
/// One call, in the order it arrived. <see cref="Argument"/> is the folder name, the Drive file id
/// or the session URI, depending on the operation.
/// </summary>
public sealed record FakeDriveCall(
    FakeDriveOperation Operation,
    Guid AccountId,
    string? Argument,
    long Offset,
    long Length);

public sealed record FakeDriveFolder(string Id, Guid AccountId, string Name, string? ParentFolderId);

/// <summary>An open resumable upload, and everything Drive would remember about it.</summary>
public sealed class FakeUploadSession
{
    public required Uri Uri { get; init; }

    public required Guid AccountId { get; init; }

    public required DriveUploadRequest Request { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }

    public long ConfirmedLength { get; set; }

    /// <summary>Set once the last chunk lands.</summary>
    public string? CompletedFileId { get; set; }

    /// <summary>Every byte that was written into this session, in order.</summary>
    public MemoryStream Received { get; } = new();
}

public sealed class FakeDriveFile
{
    public required DriveFileMetadata Metadata { get; init; }

    public required Guid AccountId { get; init; }

    public required byte[] Content { get; init; }

    /// <summary>
    /// Which folder the file is in. Settable, because the trash is a move and a move is the only
    /// thing in this product that changes it.
    /// </summary>
    public string? ParentFolderId { get; set; }
}

/// <summary>
/// An in-memory Google Drive.
///
/// It exists because this product must be provable without a network: which account took an upload,
/// what a rate limit does to a chunk, what a dead session URI looks like to the client. Everything
/// it is asked is recorded in <see cref="Calls"/>; everything it holds is on
/// <see cref="Sessions"/>, <see cref="Files"/> and <see cref="Folders"/>.
///
/// How to drive it:
/// <list type="bullet">
/// <item><c>FailNext(op, ex)</c> — the next call to that operation throws, once.</item>
/// <item><c>FailAlways(op, ex)</c> / <c>ClearFailure(op)</c> — every call throws until cleared.</item>
/// <item><c>RateLimitNext(op, retryAfter)</c> — the same, with a
/// <see cref="DriveRateLimitedException"/> already built.</item>
/// <item><c>SessionLifetime</c> and <c>ExpireSessions()</c> — for the resumable-session expiry path.</item>
/// <item><c>Clock</c> — swap in a fixed clock to control expiry deterministically.</item>
/// <item><c>Quota</c> — what <see cref="GetStorageQuotaAsync"/> reports.</item>
/// </list>
///
/// It is loud rather than forgiving: an out-of-order chunk or a body whose length disagrees with the
/// declared chunk length throws instead of quietly doing something reasonable, because in a test a
/// silent recovery is a bug that ships.
///
/// One instance per test. It is not thread-safe, and no part of it should be shared across tests
/// running in parallel.
/// </summary>
public sealed class FakeDriveClient : IDriveClient
{
    private readonly List<FakeDriveCall> _calls = [];
    private readonly List<FakeDriveFolder> _folders = [];
    private readonly Dictionary<Uri, FakeUploadSession> _sessions = [];
    private readonly Dictionary<string, FakeDriveFile> _files = [];
    private readonly Dictionary<FakeDriveOperation, Queue<Exception>> _failOnce = [];
    private readonly Dictionary<FakeDriveOperation, Exception> _failAlways = [];

    private int _nextId;

    /// <summary>Controls resumable-session expiry. Replace it to make expiry deterministic.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>How long a new resumable session lives. Drive's own is about a week.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>What <see cref="GetStorageQuotaAsync"/> reports. 5 TB free, as sold.</summary>
    public DriveStorageQuota Quota { get; set; } = new(5L * 1024 * 1024 * 1024 * 1024, 0);

    public IReadOnlyList<FakeDriveCall> Calls => _calls;

    public IReadOnlyList<FakeDriveFolder> Folders => _folders;

    public IReadOnlyDictionary<Uri, FakeUploadSession> Sessions => _sessions;

    public IReadOnlyDictionary<string, FakeDriveFile> Files => _files;

    /// <summary>
    /// The exact <see cref="Stream"/> instance handed to the last <see cref="WriteChunkAsync"/>.
    ///
    /// Reference equality against the stream the caller was given is how a test proves the upload
    /// path forwarded the request body rather than copying it: a coordinator that buffered would
    /// hand a different object down.
    /// </summary>
    public Stream? LastChunkStream { get; private set; }

    public void FailNext(FakeDriveOperation operation, Exception exception)
    {
        if (!_failOnce.TryGetValue(operation, out var queue))
        {
            queue = new Queue<Exception>();
            _failOnce[operation] = queue;
        }

        queue.Enqueue(exception);
    }

    public void FailAlways(FakeDriveOperation operation, Exception exception) =>
        _failAlways[operation] = exception;

    public void ClearFailure(FakeDriveOperation operation)
    {
        _failAlways.Remove(operation);
        _failOnce.Remove(operation);
    }

    public void RateLimitNext(FakeDriveOperation operation, TimeSpan? retryAfter = null) =>
        FailNext(operation, new DriveRateLimitedException(
            $"{operation} was rate limited by the fake Drive.", retryAfter));

    /// <summary>Ages every open session past its expiry, without moving the clock.</summary>
    public void ExpireSessions()
    {
        var past = Clock.GetUtcNow().AddSeconds(-1);
        foreach (var session in _sessions.Values)
        {
            session.ExpiresAt = past;
        }
    }

    /// <summary>Puts a file in the fake account so the download path has something to serve.</summary>
    public DriveFileMetadata SeedFile(
        Guid accountId,
        string fileId,
        string name,
        string mimeType,
        byte[] content)
    {
        var now = Clock.GetUtcNow();

        // A real checksum of the real bytes, because the migration's whole verification is comparing
        // what it streamed against what Drive says landed — a fake that returned a constant would
        // pass that check no matter what it had actually stored.
        var metadata = new DriveFileMetadata(
            fileId, name, mimeType, content.LongLength, now, now, Md5Of(content));

        _files[fileId] = new FakeDriveFile { Metadata = metadata, AccountId = accountId, Content = content };
        return metadata;
    }

    public Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.BeginResumableUpload, accountId, request.FileName, 0, request.SizeBytes);

        var uri = new Uri($"https://upload.fake-drive.invalid/session/{Next()}");
        var session = new FakeUploadSession
        {
            Uri = uri,
            AccountId = accountId,
            Request = request,
            ExpiresAt = Clock.GetUtcNow().Add(SessionLifetime),
        };

        _sessions[uri] = session;

        return Task.FromResult(new DriveResumableSession(uri, session.ExpiresAt));
    }

    public async Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.WriteChunk, Guid.Empty, sessionUri.ToString(), offset, length);
        LastChunkStream = content;

        var session = RequireLiveSession(sessionUri);

        if (offset != session.ConfirmedLength)
        {
            // Drive acknowledges one contiguous prefix per session, always anchored at zero. A
            // writer that jumps ahead is not slow, it is wrong.
            throw new DriveApiException(
                $"Chunk offset {offset} does not continue this session, which has "
                + $"{session.ConfirmedLength} bytes confirmed.");
        }

        var before = session.Received.Length;
        await content.CopyToAsync(session.Received, cancellationToken);
        var copied = session.Received.Length - before;

        if (copied != length)
        {
            throw new DriveApiException(
                $"The chunk body carried {copied} bytes but declared {length}.");
        }

        session.ConfirmedLength += length;

        // «/*»: the writer has not said how long the file is yet, so this cannot be the chunk that
        // finishes it however much has arrived. Completing here would hand back a file id for a
        // half-written snapshot and make the one mode this exists to model untestable.
        if (totalSize == UploadChunking.UnknownTotal)
        {
            if (length % UploadChunking.DriveChunkMultiple != 0)
            {
                // Drive would take this and then silently stop acknowledging bytes. Loud, like the
                // rest of this fake: a partial chunk is only allowed as the last one, and a chunk
                // that declines to name the total is by definition not the last one.
                throw new DriveApiException(
                    $"A chunk of {length} bytes with no declared total is not a multiple of "
                    + $"{UploadChunking.DriveChunkMultiple}, so it can be neither a middle chunk nor "
                    + "a final one.");
            }

            return new DriveChunkOutcome(session.ConfirmedLength, null);
        }

        if (session.ConfirmedLength < totalSize) return new DriveChunkOutcome(session.ConfirmedLength, null);

        var now = Clock.GetUtcNow();
        var fileId = $"drive-file-{Next()}";
        var landed = session.Received.ToArray();

        var metadata = new DriveFileMetadata(
            fileId,
            session.Request.FileName,
            session.Request.MimeType,
            session.ConfirmedLength,
            now,
            now,
            Md5Of(landed));

        _files[fileId] = new FakeDriveFile
        {
            Metadata = metadata,
            AccountId = session.AccountId,
            Content = landed,
        };
        session.CompletedFileId = fileId;

        return new DriveChunkOutcome(session.ConfirmedLength, metadata);
    }

    public Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.GetConfirmedLength, Guid.Empty, sessionUri.ToString(), 0, totalSize);

        return Task.FromResult(RequireLiveSession(sessionUri).ConfirmedLength);
    }

    public Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.OpenDownload, accountId, driveFileId, 0, 0);

        if (!_files.TryGetValue(driveFileId, out var file))
        {
            throw new DriveApiException($"No file {driveFileId} in the fake Drive.");
        }

        var total = file.Content.LongLength;

        // 206 is decided by the request, not by the size of the answer. Drive answers any range it
        // can satisfy with a 206 and a Content-Range naming the slice — «Range: bytes=0-» on a
        // 4096-byte file comes back «206 · bytes 0-4095/4096», which is the commonest range a
        // browser sends. Judging partialness by whether the slice happens to be the whole file
        // would answer 200 there and quietly leave the busiest branch of the download path unproven.
        var honoured = ResolveRange(rangeHeader, total);
        var start = honoured?.Start ?? 0;
        var length = honoured is { } range ? range.End - range.Start + 1 : total;
        var slice = file.Content.AsSpan((int)start, (int)length).ToArray();

        return Task.FromResult(new DriveDownload(
            new MemoryStream(slice, writable: false),
            file.Metadata.MimeType,
            slice.LongLength,
            honoured is { } served ? $"bytes {served.Start}-{served.End}/{total}" : null,
            honoured is not null,
            NoopAsyncDisposable.Instance));
    }

    public Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.EnsureFolder, accountId, folderName, 0, 0);

        var existing = _folders.FirstOrDefault(
            f => f.AccountId == accountId && f.Name == folderName && f.ParentFolderId == parentFolderId);

        if (existing is not null) return Task.FromResult(existing.Id);

        var folder = new FakeDriveFolder($"folder-{Next()}", accountId, folderName, parentFolderId);
        _folders.Add(folder);

        return Task.FromResult(folder.Id);
    }

    public Task MoveAsync(
        Guid accountId,
        string driveFileId,
        string? fromFolderId,
        string toFolderId,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.Move, accountId, driveFileId, 0, 0);

        if (!_files.TryGetValue(driveFileId, out var file))
        {
            throw new DriveApiException($"The fake Drive holds no file {driveFileId}.");
        }

        file.ParentFolderId = toFolderId;

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid accountId, string driveFileId, CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.Delete, accountId, driveFileId, 0, 0);

        // Already gone is success, the way it is against Drive: the purge wants the row and the
        // bytes to agree, and a delete that finds nothing has achieved exactly that.
        _files.Remove(driveFileId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Makes the next verification disagree with what was actually stored.
    ///
    /// <para>The one failure a migration exists to survive: storage reports an upload complete and
    /// then says something else about it. Contrived on purpose — it is not a thing Drive does often,
    /// and it is the thing that costs somebody their only copy if the code believes it.</para>
    /// </summary>
    public bool CorruptNextVerification { get; set; }

    public Task<DriveFileMetadata?> GetFileAsync(
        Guid accountId,
        string driveFileId,
        CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.GetFile, accountId, driveFileId, 0, 0);

        if (CorruptNextVerification)
        {
            CorruptNextVerification = false;

            return Task.FromResult<DriveFileMetadata?>(
                _files.TryGetValue(driveFileId, out var suspect) && suspect.AccountId == accountId
                    ? suspect.Metadata with { Md5Checksum = new string('0', 32) }
                    : null);
        }

        // Scoped to the account, like the real one: a file id belonging to another account is not
        // this account's file, and answering otherwise would let a migration «verify» a copy it
        // never made.
        return Task.FromResult(
            _files.TryGetValue(driveFileId, out var file) && file.AccountId == accountId
                ? file.Metadata
                : null);
    }

    private static string Md5Of(byte[] content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content));

    public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken)
    {
        Record(FakeDriveOperation.GetStorageQuota, accountId, null, 0, 0);

        return Task.FromResult(Quota);
    }

    private FakeUploadSession RequireLiveSession(Uri sessionUri)
    {
        if (!_sessions.TryGetValue(sessionUri, out var session))
        {
            throw new DriveUploadSessionExpiredException($"No resumable session at {sessionUri}.");
        }

        if (Clock.GetUtcNow() >= session.ExpiresAt)
        {
            throw new DriveUploadSessionExpiredException($"The session at {sessionUri} has expired.");
        }

        return session;
    }

    /// <summary>
    /// The byte range Drive would honour, or null when there is none to honour — no <c>Range</c>
    /// header at all, or one in a unit this fake does not model, both of which Drive answers with
    /// the whole file under a 200.
    ///
    /// A range it understands but cannot satisfy is Drive's 416, which <see cref="DriveDownload"/>
    /// has no shape for, so it throws. Loud rather than forgiving, like the rest of this fake:
    /// serving something plausible instead would be a silent recovery, and in a test that is a bug
    /// that ships.
    /// </summary>
    private static (long Start, long End)? ResolveRange(string? rangeHeader, long total)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return null;

        const string prefix = "bytes=";
        var value = rangeHeader.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var spec = value[prefix.Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;

        var head = spec[..dash];
        var tail = spec[(dash + 1)..];
        var last = total - 1;

        long start;
        long end;

        if (head.Length == 0)
        {
            // "bytes=-500": the last 500 bytes.
            start = Math.Max(0, total - long.Parse(tail, CultureInfo.InvariantCulture));
            end = last;
        }
        else
        {
            start = long.Parse(head, CultureInfo.InvariantCulture);
            end = tail.Length == 0 ? last : Math.Min(last, long.Parse(tail, CultureInfo.InvariantCulture));
        }

        if (start < 0 || start > last || end < start)
        {
            throw new DriveApiException(
                $"Range '{rangeHeader}' cannot be satisfied against {total} bytes. Real Drive "
                + "answers 416 here, and this fake does not model that response.");
        }

        return (start, end);
    }

    private void Record(FakeDriveOperation operation, Guid accountId, string? argument, long offset, long length)
    {
        _calls.Add(new FakeDriveCall(operation, accountId, argument, offset, length));

        if (_failAlways.TryGetValue(operation, out var always)) throw always;

        if (_failOnce.TryGetValue(operation, out var queue) && queue.Count > 0) throw queue.Dequeue();
    }

    private int Next() => ++_nextId;

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
