using DriveUnion.Core.Uploads;

namespace DriveUnion.Core.Application;

public sealed record BeginUploadRequest(string FileName, string MimeType, long SizeBytes);

public sealed record BeginUploadResult(Guid SessionId, int ChunkSize);

public sealed record UploadProgress(
    Guid SessionId,
    long BytesConfirmed,
    long SizeBytes,
    UploadSessionStatus Status,
    Guid? StoredFileId,
    string? FailureReason);

/// <summary>
/// Browser → OVH → Google, one chunk at a time.
///
/// The browser never talks to Google directly: the resumable session URI is a bearer capability over
/// the operator's Drive account, and the OVH box is the fast path to Google that the customer's own
/// connection is not.
///
/// <see cref="WriteChunkAsync"/> receives the request body as a forward-only stream and must forward
/// it to Drive without buffering. A 96 GB upload that is spooled anywhere is a 96 GB bug.
/// </summary>
public interface IUploadCoordinator
{
    Task<BeginUploadResult> BeginAsync(
        Guid tenantId,
        BeginUploadRequest request,
        CancellationToken cancellationToken);

    Task<UploadProgress> WriteChunkAsync(
        Guid tenantId,
        Guid sessionId,
        Stream content,
        long offset,
        long length,
        CancellationToken cancellationToken);

    /// <summary>
    /// Where to resume. Asks Google what it has actually acknowledged rather than trusting our own
    /// record of what was sent.
    /// </summary>
    Task<UploadProgress> GetProgressAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken);
}
