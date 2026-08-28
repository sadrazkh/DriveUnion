namespace DriveUnion.Core.Storage;

public enum FileLockStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// Locking a file that is already here.
///
/// <para>A customer who uploaded something in the clear and then decided it should not have been in
/// the clear had exactly one remedy: download it, delete it, and upload it again with the box
/// ticked. On a phone, over a link, or for anything large, that is not a remedy. This is the server
/// doing it for them — the same trade the link-upload path makes, and for the same reason.</para>
///
/// <para><b>What the server learns.</b> Nothing it did not already have. The plaintext is already in
/// the operator's Drive; that is what "uploaded in the clear" means. What is new is that the content
/// key passes through this process for as long as the job runs, in memory, in
/// <see cref="Uploads.ContentKeyring"/> — never on this row and never on disk. The passphrase never
/// arrives at all: the browser derives the key, wraps it, and sends the wrapped copy for the row and
/// the raw copy for the work. The four custody fields below are the wrapped half.</para>
///
/// <para><b>The order is the whole feature.</b> The plaintext copy in Drive is deleted only after
/// the ciphertext is complete, verified against a checksum, and the catalogue points at it. Every
/// other order loses somebody's file to a crash at the wrong moment, and this product's promise is
/// that it does not do that. <see cref="SourceDriveFileId"/> outlives the swap so a process that
/// died between the two can finish the delete rather than leaving a copy nobody knows about.</para>
/// </summary>
public sealed class FileLock
{
    public Guid Id { get; set; }

    /// <summary>
    /// The workspace, and <b>deliberately not a foreign key</b>.
    ///
    /// <para><c>TenantStorageMeter</c> detaches the tenant after <c>ExecuteUpdate</c>, and detaching
    /// a principal cascade-detaches its tracked dependents — so a row tied to it by a real key stops
    /// being written halfway through its own work, silently. <c>UploadSession</c>, <c>RemoteFetch</c>,
    /// <c>AbuseReport</c> and <c>DeletionJob</c> all have none for this reason, and this row is
    /// written on either side of a quota reservation, which is exactly when it would bite.</para>
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>The file being locked. It keeps its id, its name, its folder and its tags.</summary>
    public Guid StoredFileId { get; set; }

    /// <summary>Who asked, for the notification and for the audit. No foreign key, as above.</summary>
    public Guid? RequestedByUserId { get; set; }

    public FileLockStatus Status { get; set; }

    /// <summary>What the plaintext was, so the sealing loop knows how many segments to make.</summary>
    public long PlaintextLength { get; set; }

    /// <summary>How much ciphertext has reached Drive, for the row on the screen.</summary>
    public long BytesSealed { get; set; }

    /// <summary>
    /// The Google account both copies live in.
    ///
    /// <para>The same account on purpose: this is not a migration, and moving a file between
    /// accounts while also changing its contents would be two failures sharing one rollback.</para>
    /// </summary>
    public Guid GoogleAccountId { get; set; }

    /// <summary>
    /// The plaintext copy, which must outlive the swap.
    ///
    /// <para>Kept until the delete has actually happened rather than being read back off the file
    /// row, because the file row is repointed at the ciphertext the moment the swap lands — after
    /// which nothing else in the database remembers where the readable copy was. A process that
    /// stops between the swap and the delete would otherwise leave a plaintext copy of a file the
    /// customer believes is locked, in the operator's Drive, with nothing able to find it.</para>
    /// </summary>
    public string? SourceDriveFileId { get; set; }

    /// <summary>The ciphertext, once Drive has accepted all of it.</summary>
    public string? SealedDriveFileId { get; set; }

    /// <summary>
    /// Whether the plaintext has been deleted. The last step, and the one that must never run early.
    /// </summary>
    public bool SourceRemoved { get; set; }

    // ── the custody fields, as decided in the browser ───────────────────────────────────────────
    //
    // The same four RemoteFetch carries, for the same reason and in the same order. What is here is
    // what it takes to *ask* for the key later; what is deliberately not here is anything that opens
    // the file.

    /// <summary>Base64. Salts the passphrase, so two people choosing the same one differ.</summary>
    public string? KdfSalt { get; set; }

    public int KdfIterations { get; set; }

    /// <summary>Base64: the content key, sealed under what the customer typed.</summary>
    public string? WrappedKey { get; set; }

    /// <summary>Base64. The per-file half of every segment's nonce.</summary>
    public string? NoncePrefix { get; set; }

    public int Attempts { get; set; }

    /// <summary>Said to the customer, so it names no Google account and no internal detail.</summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Three, matching <c>FileRelocation.MaxAttempts</c>.
    ///
    /// <para>A lock that keeps failing is not retried for ever: every attempt reads the whole file
    /// out of Drive and writes a whole encrypted copy back, so a file that cannot be sealed is an
    /// expensive thing to keep discovering.</para>
    /// </summary>
    public const int MaxAttempts = 3;

    public const int MaxFailureReasonLength = 512;
}
