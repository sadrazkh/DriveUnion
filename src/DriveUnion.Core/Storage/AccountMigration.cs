namespace DriveUnion.Core.Storage;

public enum AccountMigrationStatus
{
    /// <summary>Accepted and waiting for the worker.</summary>
    Pending = 0,

    /// <summary>The worker is moving files.</summary>
    Running = 1,

    /// <summary>A pass found nothing left to move. Some files may still have failed.</summary>
    Completed = 2,

    /// <summary>Stopped before it finished, by the operator or by something it could not get past.</summary>
    Failed = 3,

    /// <summary>The operator asked it to stop. Files already moved stay moved.</summary>
    Cancelled = 4,
}

/// <summary>
/// Emptying one Google account into another, one file at a time.
///
/// <para><b>Why this exists.</b> The pool has always been able to hold several accounts and route
/// uploads across them by free space, but nothing could ever move a file once it landed. That is
/// fine until an account fills up, gets suspended, or has to be retired — at which point the
/// operator owns files they cannot relocate and a pool they cannot rebalance.</para>
///
/// <para><b>Why the file list is not stored.</b> «What is left on account A» is a query against the
/// catalogue, so the worker asks it again on every pass rather than snapshotting once. A snapshot
/// taken at the start would miss anything uploaded while the drain was running and would keep
/// pointing at files somebody deleted halfway through. The drain is over when a pass finds
/// nothing.</para>
///
/// <para><b>Why moving is not copying-and-deleting in one step.</b> A move that deleted the source
/// before the target was verified would, on the one failure that matters, destroy the only copy of
/// somebody's file. So the copy is verified against Drive's own checksum, the catalogue is swapped,
/// and the source is left standing for <see cref="FileRelocation.Grace"/> — long enough that a
/// download already streaming from it is not cut off mid-transfer — before a sweeper removes it.
/// </para>
/// </summary>
public sealed class AccountMigration
{
    public Guid Id { get; set; }

    /// <summary>The account being emptied.</summary>
    public Guid SourceAccountId { get; set; }

    /// <summary>Where its files are going. Must be a different, healthy account with room.</summary>
    public Guid TargetAccountId { get; set; }

    public AccountMigrationStatus Status { get; set; }

    public int FilesMoved { get; set; }

    /// <summary>
    /// Files this migration gave up on after <c>MaxAttemptsPerFile</c> tries.
    ///
    /// <para>Counted rather than fatal: one file that Drive will not hand over must not strand the
    /// other thirty thousand. The per-file rows say which ones, so the operator can act on them.
    /// </para>
    /// </summary>
    public int FilesFailed { get; set; }

    public long BytesMoved { get; set; }

    /// <summary>Why it stopped, when it stopped badly. Operator-facing and never shown to a tenant.</summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Enough for a Drive error and the sentence around it.</summary>
    public const int MaxFailureReasonLength = 512;
}

public enum FileRelocationStatus
{
    /// <summary>The target copy is verified and the catalogue points at it. The source still exists.</summary>
    Moved = 0,

    /// <summary>The source copy has been deleted. The move is finished.</summary>
    SourceRemoved = 1,

    /// <summary>Given up on. <see cref="FileRelocation.Attempts"/> says how many times.</summary>
    Failed = 2,
}

/// <summary>
/// One file's journey between two accounts, and the record that lets the source be deleted later.
///
/// <para>A row per file rather than a counter, for three things a counter cannot do: resume a drain
/// of thirty thousand files exactly where it stopped, tell the operator <i>which</i> ones failed,
/// and remember where the old copy is so a sweeper can remove it after the grace period. That last
/// one is the reason this table cannot be derived — once the catalogue points at the target, nothing
/// else in the product knows the source copy ever existed.</para>
/// </summary>
public sealed class FileRelocation
{
    public Guid Id { get; set; }

    public Guid MigrationId { get; set; }

    public Guid StoredFileId { get; set; }

    /// <summary>Where the old copy is, so it can be deleted once nothing is reading it.</summary>
    public Guid SourceAccountId { get; set; }

    /// <summary>The Drive id on the source account. Not the one the catalogue holds after a move.</summary>
    public required string SourceDriveFileId { get; set; }

    public Guid TargetAccountId { get; set; }

    /// <summary>The Drive id on the target, once there is one.</summary>
    public string? TargetDriveFileId { get; set; }

    public FileRelocationStatus Status { get; set; }

    public int Attempts { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>When the catalogue was swapped. The grace period is measured from here.</summary>
    public DateTimeOffset? MovedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How many times one file is tried before the drain moves on without it.
    ///
    /// <para>Three, and then it is somebody's problem rather than the loop's. A file Drive refuses
    /// for a reason that will not change — a shortcut, a Google-native document, something the
    /// account lost access to — would otherwise be retried for ever while the rest of the account
    /// waits behind it.</para>
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// How long the source copy is left standing after the catalogue stops pointing at it.
    ///
    /// <para>A download that was already streaming from the source when the swap happened is holding
    /// an open response from Google, and deleting the file underneath it is a transfer that dies at
    /// eighty per cent for no reason the visitor can see. Six hours is longer than any single
    /// transfer this product serves and short enough that a drain actually frees the account the
    /// same day.</para>
    ///
    /// <para>Nothing is at risk during the wait. The target copy is verified and live; this is one
    /// duplicate occupying space that the sweeper will take.</para>
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(6);
}
