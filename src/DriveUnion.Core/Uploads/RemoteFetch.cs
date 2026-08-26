namespace DriveUnion.Core.Uploads;

public enum RemoteFetchStatus
{
    /// <summary>Accepted and waiting for a worker.</summary>
    Queued = 0,

    /// <summary>A worker is pulling it.</summary>
    Running = 1,

    Completed = 2,
    Failed = 3,

    /// <summary>The customer asked for it to stop.</summary>
    Cancelled = 4,
}

/// <summary>
/// A file the customer asked us to go and get, rather than sending themselves.
///
/// <para><b>What it is for.</b> A 40 GB file that already exists somewhere on the internet does not
/// need to travel down a domestic connection and back up again. The server pulls it directly, and
/// the customer's machine can be asleep by then — which is the whole point, and the reason this is a
/// row and a worker rather than something a browser tab holds open.</para>
///
/// <para><b>What it is not.</b> This is the one feature in the product where a customer chooses an
/// address the server then dials, so <see cref="RemoteAddressPolicy"/> and
/// <c>GuardedFetchHandler</c> are not incidental to it — they are it. Nothing here may be reached
/// without going through them.</para>
///
/// <para><b>Why the URL is stored and the credentials are not.</b> A URL with a username and
/// password in it is refused rather than stripped: it would be logged, sat on this row, and sent to
/// whatever the host turned out to be.</para>
/// </summary>
public sealed class RemoteFetch
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Whose folder the file lands in, exactly as an ordinary upload's does.</summary>
    public Guid? OwnerUserId { get; set; }

    public required string Url { get; set; }

    /// <summary>
    /// What it will be called. Taken from <c>Content-Disposition</c>, else the URL's last path
    /// segment, else a name we make up — and never null once the fetch has started.
    /// </summary>
    public string? FileName { get; set; }

    public RemoteFetchStatus Status { get; set; }

    /// <summary>What the source said it would send, once it has been asked. Zero until then.</summary>
    public long SizeBytes { get; set; }

    /// <summary>What has actually reached storage, for a progress figure that is not a guess.</summary>
    public long BytesFetched { get; set; }

    /// <summary>The upload session this is streaming into, so a restart can pick it up.</summary>
    public Guid? UploadSessionId { get; set; }

    /// <summary>The file, once there is one.</summary>
    public Guid? StoredFileId { get; set; }

    /// <summary>Said to the customer, so it names no host of ours and no internal detail.</summary>
    public string? FailureReason { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// How many times a fetch is retried before it is somebody's problem.
    ///
    /// <para>Three. A source that is down for a minute is worth another go; one that is refusing, or
    /// gone, or lying about its length will do the same thing on the fourth attempt as on the
    /// first.</para>
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Long enough for a real filename and short enough for a column.</summary>
    public const int MaxFileNameLength = 512;

    public const int MaxFailureReasonLength = 512;

    /// <summary>
    /// How many fetches one workspace may have in flight or waiting.
    ///
    /// <para>Without a cap this is a free bandwidth proxy: paste a thousand URLs and the operator
    /// pays for a thousand transfers they did not choose to make. Five is enough that a person
    /// queueing a folder's worth of links is not fighting the product, and small enough that one
    /// workspace cannot occupy the worker.</para>
    /// </summary>
    public const int MostInFlightPerTenant = 5;
}
