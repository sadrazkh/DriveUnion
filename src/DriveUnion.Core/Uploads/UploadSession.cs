namespace DriveUnion.Core.Uploads;

public enum UploadSessionStatus
{
    InProgress = 0,
    Completed = 1,
    Failed = 2,
    Abandoned = 3,
}

/// <summary>
/// One in-flight upload: the browser sends chunks to us, we forward each one into the Drive
/// resumable session whose URI is held here.
///
/// The row exists because the session outlives a single HTTP request. Without it, a 96 GB upload
/// that drops at 90% has nowhere to resume from.
/// </summary>
public sealed class UploadSession
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GoogleAccountId { get; set; }

    public required string FileName { get; set; }

    public required string MimeType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// Google's resumable session endpoint. A capability, not a reference: anyone holding it can
    /// write into the operator's Drive account, so it never leaves the server.
    /// </summary>
    public required string DriveResumableUri { get; set; }

    /// <summary>Bytes Google has acknowledged, not bytes we have sent.</summary>
    public long BytesConfirmed { get; set; }

    public UploadSessionStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Drive resumable sessions are good for about a week. Past this the URI is dead and the client
    /// has to start over — which is worth telling it plainly rather than letting each chunk 404.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// The file this session produced, set on the chunk that completed it.
    ///
    /// Without it the only way to answer "which file did this upload become?" on a later request is
    /// to match tenant, account, name and byte count — which is a guess, and an ambiguous one for a
    /// customer who uploads the same file twice.
    /// </summary>
    public Guid? StoredFileId { get; set; }

    public bool IsResumable(DateTimeOffset now) =>
        Status == UploadSessionStatus.InProgress && now < ExpiresAt;
}
