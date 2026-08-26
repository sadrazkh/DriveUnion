using DriveUnion.Core.Uploads;

namespace DriveUnion.Core.Application;

/// <param name="SizeBytes">
/// What will be sent, which for an encrypted upload is the <i>ciphertext</i> length — longer than
/// the file by one tag per segment. The quota is spent on what is stored, so this is the number the
/// plan is checked against, and <c>Encryption.PlaintextLength</c> is the one the customer is shown.
/// </param>
/// <param name="Encryption">
/// Present when the browser encrypted this file, and carried to storage untouched. Null is a plain
/// upload.
/// </param>
public sealed record BeginUploadRequest(
    string FileName,
    string MimeType,
    long SizeBytes,
    EncryptionHeader? Encryption = null);

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
    /// <param name="ownerUserId">
    /// Whose folder the file goes into. Server-derived from the signed-in principal, and deliberately
    /// a parameter rather than a field on <see cref="BeginUploadRequest"/> — that record is bound from
    /// the request body, and a user id a caller can name is a user id a caller can name somebody
    /// else's.
    ///
    /// <para>Null where there genuinely is no user, which resolves to the tenant folder: the layout
    /// every file used before uploads were separated per person.</para>
    /// </param>
    Task<BeginUploadResult> BeginAsync(
        Guid tenantId,
        Guid? ownerUserId,
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
