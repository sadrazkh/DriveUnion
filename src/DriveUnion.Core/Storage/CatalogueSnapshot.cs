namespace DriveUnion.Core.Storage;

public enum CatalogueSnapshotStatus
{
    /// <summary>Asked for and waiting for the worker. Either the schedule's turn or a button.</summary>
    Pending = 0,

    /// <summary>Being written. A row left here is a run the process died in the middle of.</summary>
    Running = 1,

    /// <summary>At least one copy is in the pool, verified. <see cref="CatalogueSnapshot.CopiesMade"/> says how many.</summary>
    Completed = 2,

    /// <summary>Every account refused it, or it ran out of attempts. The reason is on the row.</summary>
    Failed = 3,
}

/// <summary>
/// One export of the catalogue into the pool the catalogue describes.
///
/// <para><b>The failure this exists for.</b> Customer files live in the operator's Google accounts
/// and are as safe as Google is. What is <i>not</i> safe is the mapping — this customer's
/// «quarterly.mp4» is Drive file <c>1abc…</c> on account A2 — which exists in Postgres and nowhere
/// else. Lose that database and every byte is still sitting on Google's servers and not one of them
/// is reachable: nothing knows whose they are, what they were called, or which account holds them.
/// A total loss with a completely intact storage backend, and until this there was no export of the
/// mapping anywhere.</para>
///
/// <para><b>Why the snapshot goes into the pool rather than somewhere else.</b> The pool is the one
/// store this product already has, already pays for, and already knows is up — a backup that needs a
/// second provider is a backup that stops working the first time somebody rotates a key nobody
/// remembers. Storage that carries its own index also survives the case that matters: whoever is
/// holding the accounts can rebuild the product from them without holding anything else.</para>
///
/// <para><b>Why a row per run.</b> The snapshot is a file in a Drive folder, and
/// <c>IDriveClient</c> cannot list a folder — so without these rows nothing knows what is out there,
/// nothing can delete last month's, and the operator has no way to tell a backup that ran from one
/// that has been silently failing since March. See <see cref="CatalogueSnapshotCopy"/> for the other
/// half, which is where each copy actually landed.</para>
/// </summary>
public sealed class CatalogueSnapshot
{
    public Guid Id { get; set; }

    /// <summary>
    /// The file name in the pool: <c>catalogue-20260827-041500Z.jsonl.gz</c>.
    ///
    /// <para>UTC and sortable, because the person reading it is looking at a folder of them in
    /// Google's own web UI on the worst day of their year and needs «the newest one» to be obvious
    /// without translating a timezone.</para>
    /// </summary>
    public required string Name { get; set; }

    public CatalogueSnapshotStatus Status { get; set; }

    /// <summary>
    /// True when an operator pressed the button rather than the schedule coming round.
    ///
    /// <para>A bool rather than a trigger enum with two members: what the screen says is «by hand»
    /// or «scheduled», and the only reason to know is that a hand-made one is usually somebody
    /// about to do something risky and wanting a fresh index first.</para>
    /// </summary>
    public bool ByHand { get; set; }

    /// <summary>Who pressed it. Null for the schedule, which is nobody.</summary>
    public Guid? RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int Attempts { get; set; }

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // What was in it. Counted as the rows go past rather than queried afterwards: the numbers have
    // to describe the file that was written, and a COUNT(*) run a minute later describes the
    // database instead. They are also the only cheap way to see the shape of a disaster — a
    // snapshot whose file count halved overnight is worth looking at before it is restored from.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    public int TenantCount { get; set; }

    public int AccountCount { get; set; }

    public int FolderCount { get; set; }

    public int FileCount { get; set; }

    /// <summary>
    /// How many of those files carry an encryption header.
    ///
    /// <para>Its own number because it is the count of files that are unopenable for ever if this
    /// snapshot is wrong — see <c>FileEncryption</c>, and the format's note on why the headers are
    /// in here at all.</para>
    /// </summary>
    public int EncryptionCount { get; set; }

    /// <summary>The size of the compressed file, which is the same for every copy of one run.</summary>
    public long SizeBytes { get; set; }

    /// <summary>How many accounts this run tried to put a copy on.</summary>
    public int CopiesWanted { get; set; }

    /// <summary>
    /// How many it managed. Fewer than wanted is not a failure — it is the pool being smaller or
    /// sicker than the target — but it is exactly the number an operator should be able to see.
    /// </summary>
    public int CopiesMade { get; set; }

    /// <summary>Why it stopped, when it stopped badly. Operator-facing and never shown to a tenant.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// How often a snapshot is taken.
    ///
    /// <para>A day. The thing being protected against is losing the database, and the cost of a
    /// stale index is the files uploaded since it was written — those rows are gone either way, and
    /// what a day buys back is everything older. Hourly would multiply the pool's file count and the
    /// Drive calls by twenty-four to shorten a window that only matters in a disaster that has
    /// already happened.</para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// How many runs are kept before the oldest are deleted from the pool.
    ///
    /// <para>Fourteen — two weeks. Not «the newest», because the failure that needs an old one is
    /// corruption rather than loss: a bad migration or a bad delete is usually noticed days later,
    /// and by then the newest snapshot faithfully records the damage. Two weeks is long enough to
    /// cover a holiday and short enough that the pool is not being spent on indexes.</para>
    /// </summary>
    public const int Keep = 14;

    /// <summary>
    /// How many accounts get a copy of each run.
    ///
    /// <para>Two, whenever the pool has two healthy accounts. One copy on one account answers «the
    /// database is gone» and not «this account is gone», and those are the same afternoon more often
    /// than they are not — an account that is suspended, or whose OAuth grant dies, takes the index
    /// of every <i>other</i> account's files with it. The second copy costs one more upload of a
    /// file measured in megabytes.</para>
    /// </summary>
    public const int Copies = 2;

    /// <summary>
    /// How many times a run is retried before it is left as failed for the operator.
    ///
    /// <para>Three. A rate limit or a token that expired mid-write is worth another go; an account
    /// that refuses three times in a row is a thing to be told about rather than retried until the
    /// schedule comes round again anyway.</para>
    /// </summary>
    public const int MaxAttempts = 3;

    public const int MaxNameLength = 128;

    /// <summary>Enough for a Drive error and the sentence around it, like the migration's.</summary>
    public const int MaxFailureReasonLength = 512;
}

/// <summary>
/// One snapshot, on one account.
///
/// <para>A row per copy because <c>IDriveClient</c> has no «list this folder»: the Drive file id
/// recorded here is the only thing in the product that knows the file exists. Without it the pool
/// fills with snapshots nothing can delete, and — worse on the day it matters — an operator cannot
/// be told <i>which</i> accounts to go and look in.</para>
///
/// <para>Deliberately no foreign key to <c>GoogleAccount</c>, for the reason
/// <c>AccountMigration</c> gives: disconnecting an account must not delete the record that it is
/// holding a copy of the index. That record is at its most valuable precisely when the account is
/// no longer connected.</para>
/// </summary>
public sealed class CatalogueSnapshotCopy
{
    public Guid Id { get; set; }

    public Guid SnapshotId { get; set; }

    /// <summary>Which pool account is holding this copy.</summary>
    public Guid GoogleAccountId { get; set; }

    /// <summary>The Drive id of the file. What a restore opens and what the pruner deletes.</summary>
    public required string DriveFileId { get; set; }

    /// <summary>The <c>.catalogue</c> folder it sits in, so an operator can find it by hand.</summary>
    public string? DriveFolderId { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset WrittenAt { get; set; }

    /// <summary>
    /// When the pruner deleted it, or null while it is still in the pool.
    ///
    /// <para>The row outlives the file on purpose. «There was a snapshot on A2 in July and it is
    /// gone now» is a different sentence from «there has never been one», and only one of them is
    /// evidence that the backup was running.</para>
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }
}
