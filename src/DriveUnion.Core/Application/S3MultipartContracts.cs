namespace DriveUnion.Core.Application;

/// <summary>One staged part as the gateway reports it.</summary>
public sealed record S3PartSummary(int PartNumber, long SizeBytes, string ETag, DateTimeOffset UploadedAt);

public enum S3MultipartOutcome
{
    Done,

    /// <summary>No such upload id for this workspace, or it has been completed or aborted.</summary>
    NotFound,

    /// <summary>A part number outside 1…<see cref="Api.S3MultipartUpload.MaxParts"/>.</summary>
    InvalidPartNumber,

    /// <summary>The completion named a part that was never uploaded, or gave the wrong ETag.</summary>
    InvalidPart,

    /// <summary>The completion listed no parts at all.</summary>
    EmptyCompletion,

    /// <summary>The volume the parts stage on has no room left.</summary>
    NoRoom,
}

public sealed record S3MultipartResult(
    S3MultipartOutcome Outcome,
    Guid? UploadId = null,
    string? ETag = null,
    Guid? StoredFileId = null)
{
    public bool Succeeded => Outcome == S3MultipartOutcome.Done;
}

/// <summary>
/// S3 multipart uploads, staged on the operator's disk and assembled on completion.
///
/// <para>The reasoning for staging at all — and what it costs — is on
/// <see cref="Api.S3MultipartUpload"/>. Nothing in this interface is reachable without a live S3
/// credential, and every method takes <c>tenantId</c> explicitly like the rest of the product.</para>
/// </summary>
public interface IS3Multipart
{
    /// <summary>
    /// Whether this deployment can stage parts at all.
    ///
    /// <para>Read by the gateway before it opens an upload, so a deployment with no staging volume
    /// answers NotImplemented at the first call rather than accepting parts and failing at the
    /// completion — after the client has spent an hour sending them.</para>
    /// </summary>
    bool IsAvailable { get; }

    Task<S3MultipartResult> BeginAsync(
        Guid tenantId,
        Guid ownerUserId,
        string key,
        string name,
        Guid? folderId,
        string mimeType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages one part, returning the ETag the client will send back.
    ///
    /// <para>Re-uploading a part number replaces what was staged under it. S3 allows that and clients
    /// do it on retry, so a gateway that appended instead would assemble the object twice over.</para>
    /// </summary>
    Task<S3MultipartResult> StagePartAsync(
        Guid tenantId,
        Guid uploadId,
        int partNumber,
        Stream body,
        CancellationToken cancellationToken);

    /// <summary>The parts staged so far, by number — what <c>ListParts</c> answers with.</summary>
    Task<IReadOnlyList<S3PartSummary>> PartsAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Assembles the named parts, in the order given, into one file.
    ///
    /// <para>The client's list is authoritative and is checked against what was staged: a part it
    /// names that is not there, or whose ETag disagrees, refuses the whole completion rather than
    /// assembling something the client did not ask for. Parts that were staged and <i>not</i> named
    /// are discarded, which is S3's behaviour — a client may upload a part and then decide against
    /// it.</para>
    /// </summary>
    Task<S3MultipartResult> CompleteAsync(
        Guid tenantId,
        Guid uploadId,
        IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken);

    /// <summary>Throws the whole thing away, staged bytes included.</summary>
    Task<S3MultipartResult> AbortAsync(Guid tenantId, Guid uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the uploads nobody finished. Returns how many were swept.
    ///
    /// <para>Called by a background pass rather than by a request: an upload is abandoned by a client
    /// going away, which is precisely the case where nothing arrives to trigger a cleanup.</para>
    /// </summary>
    Task<int> SweepAbandonedAsync(CancellationToken cancellationToken);
}
