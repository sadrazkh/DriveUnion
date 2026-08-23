using System.Globalization;
using DriveUnion.Core.Abstractions;

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
        var metadata = new DriveFileMetadata(fileId, name, mimeType, content.LongLength, now, now);
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

        if (session.ConfirmedLength < totalSize) return new DriveChunkOutcome(session.ConfirmedLength, null);

        var now = Clock.GetUtcNow();
        var fileId = $"drive-file-{Next()}";
        var metadata = new DriveFileMetadata(
            fileId,
            session.Request.FileName,
            session.Request.MimeType,
            session.ConfirmedLength,
            now,
            now);

        _files[fileId] = new FakeDriveFile
        {
            Metadata = metadata,
            AccountId = session.AccountId,
            Content = session.Received.ToArray(),
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
        var (start, end) = ResolveRange(rangeHeader, total);
        var isPartial = rangeHeader is not null && (start != 0 || end != total - 1);
        var slice = file.Content.AsSpan((int)start, (int)(end - start + 1)).ToArray();

        return Task.FromResult(new DriveDownload(
            new MemoryStream(slice, writable: false),
            file.Metadata.MimeType,
            slice.LongLength,
            isPartial ? $"bytes {start}-{end}/{total}" : null,
            isPartial,
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

    private static (long Start, long End) ResolveRange(string? rangeHeader, long total)
    {
        var last = total == 0 ? 0 : total - 1;
        if (string.IsNullOrWhiteSpace(rangeHeader)) return (0, last);

        const string prefix = "bytes=";
        var value = rangeHeader.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (0, last);

        var spec = value[prefix.Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return (0, last);

        var head = spec[..dash];
        var tail = spec[(dash + 1)..];

        if (head.Length == 0)
        {
            // "bytes=-500": the last 500 bytes.
            var suffix = long.Parse(tail, CultureInfo.InvariantCulture);
            return (Math.Max(0, total - suffix), last);
        }

        var start = long.Parse(head, CultureInfo.InvariantCulture);
        var end = tail.Length == 0 ? last : Math.Min(last, long.Parse(tail, CultureInfo.InvariantCulture));

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
