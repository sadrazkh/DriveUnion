namespace DriveUnion.Core.Abstractions;

/// <summary>
/// Everything this product does to Google Drive.
///
/// It lives in Core, with no Google package behind it, for one reason: this machine has no Docker
/// and tests must never reach Google. Which account gets an upload, what counts as a download, how
/// a rate limit is retried and what an expired link renders are all decisions worth testing, and
/// none of them should need a network to prove.
///
/// Account credentials are resolved by the implementation from <c>accountId</c>. Nothing above this
/// interface ever holds a token.
/// </summary>
public interface IDriveClient
{
    /// <summary>
    /// Opens a resumable upload session and returns its session URI. The URI is a bearer capability
    /// over the operator's Drive account — it is stored server-side and never sent to a browser.
    /// </summary>
    Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams one chunk into an open session. <paramref name="content"/> is read once, forward
    /// only, and must not be buffered by the implementation.
    /// </summary>
    Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks Google how many bytes it has actually acknowledged, for resuming an interrupted upload.
    /// Our own record of what we sent is not evidence.
    /// </summary>
    Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the file for streaming. <paramref name="rangeHeader"/> is the client's own
    /// <c>Range</c> header, passed through untouched so Drive resolves the semantics.
    /// </summary>
    Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken);

    /// <summary>Finds or creates a folder, returning its Drive id.</summary>
    Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken);

    Task<DriveStorageQuota> GetStorageQuotaAsync(
        Guid accountId,
        CancellationToken cancellationToken);
}
