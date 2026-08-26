namespace DriveUnion.Core.Api;

/// <summary>
/// An S3 multipart upload in progress.
///
/// <para><b>Why this needs staging at all.</b> S3's protocol lets a client send parts in any order
/// and in parallel — the AWS CLI sends ten at once by default — and only says what the object is on
/// <c>CompleteMultipartUpload</c>, in an XML list of part numbers. Drive's resumable session is the
/// opposite: it acknowledges a contiguous prefix and nothing else, so part 7 cannot be written until
/// parts 1 through 6 have been. The two cannot be bridged without somewhere to put a part that
/// arrived early, so parts land on the operator's disk and are streamed into one Drive session, in
/// order, when the client says the object is complete.</para>
///
/// <para><b>What that costs, stated.</b> Disk equal to the object, for as long as the upload takes,
/// and every byte written twice — once to stage, once to Drive. That is the price of speaking
/// somebody else's protocol over a store that does not have it; the single-part path added in P12
/// still streams straight through and touches no disk, and remains what the panel and the REST API
/// use.</para>
/// </summary>
public sealed class S3MultipartUpload
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Whose folder the finished object lands in, taken from the credential.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>The object key as the client gave it, path and all.</summary>
    public required string Key { get; set; }

    /// <summary>The file name — the key's last segment — kept so completion needs no re-parsing.</summary>
    public required string Name { get; set; }

    /// <summary>Where in the customer's tree it will land. Resolved at creation, not at completion.</summary>
    public Guid? FolderId { get; set; }

    public required string MimeType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How long an abandoned upload's parts sit on disk before the sweeper takes them.
    ///
    /// <para>A client that dies between its first part and its completion leaves bytes nobody will
    /// ever ask for, and S3 answers that with lifecycle rules the customer configures. This is the
    /// version of it that needs no configuring: a day is longer than any transfer this product
    /// accepts and short enough that a failed nightly job does not fill a volume by the weekend.</para>
    /// </summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromHours(24);

    /// <summary>
    /// The most parts one upload may hold.
    ///
    /// <para>AWS's own limit is ten thousand and this matches it, so a client that computes its part
    /// size from the limit computes the same answer here. It is also what bounds the staging
    /// directory: ten thousand files in one folder is fine, and unbounded is not.</para>
    /// </summary>
    public const int MaxParts = 10_000;
}

/// <summary>One staged part. The bytes are on disk; this is what says where and how big.</summary>
public sealed class S3UploadPart
{
    public Guid UploadId { get; set; }

    /// <summary>One-based, as S3 numbers them.</summary>
    public int PartNumber { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// What the client is told and what it sends back in the completion list.
    ///
    /// <para>An MD5 of the part, which is what S3's is — and unlike the object ETags this gateway
    /// synthesises, this one can be real: the bytes are passing through anyway on their way to
    /// disk, so hashing them costs a pass over data already in hand. Clients do compare it.</para>
    /// </summary>
    public required string ETag { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
