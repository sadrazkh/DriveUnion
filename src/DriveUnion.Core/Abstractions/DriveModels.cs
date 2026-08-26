namespace DriveUnion.Core.Abstractions;

public sealed record DriveUploadRequest(
    string FileName,
    string MimeType,
    long SizeBytes,
    string? ParentFolderId);

public sealed record DriveResumableSession(
    Uri SessionUri,
    DateTimeOffset ExpiresAt);

/// <param name="Md5Checksum">
/// Drive's own fingerprint of the stored bytes, lowercase hex — and null whenever Drive did not say.
///
/// <para>Null is the ordinary case rather than a failure: what a resumable upload returns depends on
/// the fields its session was opened with, and Drive omits this entirely for its own document types.
/// It is here for one caller — the migration, which compares what it streamed against what actually
/// landed before it deletes the only other copy of somebody's file. Everywhere else it is ignored.
/// </para>
///
/// <para>MD5 because it is what Drive publishes. It is an integrity check against truncation and
/// corruption, not a defence against anybody choosing the bytes — nothing here treats it as one.
/// </para>
/// </param>
public sealed record DriveFileMetadata(
    string FileId,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedTime,
    DateTimeOffset ModifiedTime,
    string? Md5Checksum = null);

/// <summary>
/// What Google said after a chunk. <see cref="Completed"/> is non-null only on the chunk that
/// finished the file — that is the response carrying the file's metadata, and the only place we
/// learn its Drive id.
/// </summary>
public sealed record DriveChunkOutcome(
    long ConfirmedLength,
    DriveFileMetadata? Completed);

public sealed record DriveStorageQuota(
    long LimitBytes,
    long UsageBytes);

/// <summary>
/// An open response body from Drive, handed to the caller unread.
///
/// The whole download path exists to never hold a file in memory, so this type owns the underlying
/// response and hands out a <see cref="Stream"/> that has not been buffered. Copy it to the wire and
/// dispose it; do not read it into anything.
/// </summary>
public sealed class DriveDownload(
    Stream content,
    string contentType,
    long? contentLength,
    string? contentRange,
    bool isPartial,
    IAsyncDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public long? ContentLength { get; } = contentLength;

    /// <summary>Mirrored back to the client verbatim so seeking and resuming behave.</summary>
    public string? ContentRange { get; } = contentRange;

    /// <summary>True when Drive answered 206 rather than 200.</summary>
    public bool IsPartial { get; } = isPartial;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        await owner.DisposeAsync();
    }
}
