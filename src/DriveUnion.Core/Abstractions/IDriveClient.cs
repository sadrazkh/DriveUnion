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

    /// <summary>
    /// Moves a file between folders, and is how a delete reaches the trash.
    ///
    /// <para>Drive has no move: a file's parents are a collection, and moving is adding one and
    /// removing another in the same call. Both parents are passed rather than looked up, because
    /// the row already knows where the file lives and asking Drive costs a request against the
    /// 12,000-per-minute budget to learn something we wrote down.</para>
    ///
    /// <para><paramref name="fromFolderId"/> may be null for a file whose parent was never
    /// recorded — the rows that predate per-user folders. The implementation then reads the current
    /// parents from Drive, which is the one case where the extra request is unavoidable.</para>
    /// </summary>
    Task MoveAsync(
        Guid accountId,
        string driveFileId,
        string? fromFolderId,
        string toFolderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a file permanently. Not Drive's own trash — this is <c>files.delete</c>, and the
    /// bytes are gone.
    ///
    /// <para>This is the only call in the product that frees space in the operator's pool, and the
    /// only one that cannot be undone. It is reached from the purge alone, never from a customer
    /// pressing delete: that moves the file to a folder we own and keeps it for the retention
    /// window, because a customer who deletes the wrong file has a week or a month to say so.</para>
    ///
    /// <para>A file that is already gone is not an error. The purge exists to make the row and the
    /// bytes agree, and a delete that finds nothing has done exactly that.</para>
    /// </summary>
    Task DeleteAsync(
        Guid accountId,
        string driveFileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// What Drive currently holds for a file, or null when it holds nothing.
    ///
    /// <para>One request, spent at one moment: immediately before a migration would delete a
    /// customer's file from the account it is being moved off. Everything else in this product
    /// already knows what it wrote down and has no business asking Google to confirm it — but «is
    /// the copy I just made actually there, and is it the same bytes» is a question the row cannot
    /// answer, and the consequence of guessing is a file that no longer exists anywhere.</para>
    ///
    /// <para>Asks explicitly for size and md5Checksum, which the default file resource omits.</para>
    /// </summary>
    Task<DriveFileMetadata?> GetFileAsync(
        Guid accountId,
        string driveFileId,
        CancellationToken cancellationToken);

    Task<DriveStorageQuota> GetStorageQuotaAsync(
        Guid accountId,
        CancellationToken cancellationToken);
}
