namespace DriveUnion.Core.Storage;

public enum DeletionJobStatus
{
    /// <summary>Accepted and waiting for the worker.</summary>
    Pending = 0,

    /// <summary>The worker is moving files into the trash.</summary>
    Running = 1,

    /// <summary>
    /// A pass found nothing left to move. Some files may have been given up on —
    /// <see cref="DeletionJob.FilesFailed"/> says how many.
    /// </summary>
    Completed = 2,
}

/// <summary>What the customer pointed at when they pressed delete.</summary>
public enum DeletionScope
{
    /// <summary>The ticked rows in the table.</summary>
    Selection = 0,

    /// <summary>A folder and everything underneath it, however deep.</summary>
    Folder = 1,
}

/// <summary>
/// A pile of files on their way to the trash, and the record of how far that has got.
///
/// <para><b>Why this exists.</b> A delete is a Drive round trip per file — a third of a second each
/// against Google — so five thousand of them is half an hour. That cannot happen inside a form post,
/// and the two shapes it takes are the same problem: «delete this folder and everything in it», and a
/// selection bigger than the twenty a request could honestly get through.</para>
///
/// <para><b>What is already done by the time this row exists.</b> Everything the customer can see.
/// The files are stamped deleted, their links are revoked, their purge deadline is set, and a deleted
/// folder's rows are gone — all in one transaction, one statement per thing, whatever the size of the
/// job. What is left for the worker is the physical move into the operator's trash folder, which no
/// customer can observe.</para>
///
/// <para><b>Why that order is safe here and not on the single-file path.</b> <c>FileCatalog</c> moves
/// the file in Drive first and stamps the row second, because a row stamped over a move that never
/// happened would leave the bytes in the customer's home folder «with nothing left to retry». This
/// row <i>is</i> the retry: the file carries <c>PendingDeletionJobId</c> until the move lands, the
/// worker keeps coming back, and the purge deletes by Drive id and does not care which folder the
/// file was sitting in. Nothing is lost by owing the move, and what is bought is a delete that
/// returns immediately however much was selected.</para>
///
/// <para><b>Why there is no «cancel».</b> There is nothing left to stop: the deletion happened in the
/// request. A customer who changes their mind wants the file back, and that is Restore, which the
/// trash already has and which works file by file — including on a file this job has not physically
/// moved yet, because restoring clears the pending job and the worker never touches it again.</para>
///
/// <para>It carries its own <c>TenantId</c> and has no foreign key to <c>Tenant</c>, exactly like
/// <c>UploadSession</c> and <c>RemoteFetch</c> — see the model configuration for the detach hazard
/// that reasoning is about.</para>
/// </summary>
public sealed class DeletionJob
{
    public Guid Id { get; set; }

    /// <summary>
    /// Whose workspace this is. The worker runs with no request and no principal, so this row is the
    /// only place the tenant can come from — the same arrangement <c>TelegramOutbox</c> has.
    /// </summary>
    public Guid TenantId { get; set; }

    public DeletionScope Scope { get; set; }

    /// <summary>
    /// The name of the folder that was deleted, for a screen to say which one is being cleared up.
    /// Null for a selection.
    ///
    /// <para>The name and not the id: the folder row is deleted in the same transaction that makes
    /// this job, so an id here would point at nothing for the whole life of the row it is on.</para>
    /// </summary>
    public string? FolderName { get; set; }

    /// <summary>
    /// How many files this job took, counted by the statement that took them.
    ///
    /// <para>Not «how many were selected»: ids that are not this workspace's, and files already in
    /// the trash, are never matched — so this is what actually happened rather than what was asked
    /// for.</para>
    /// </summary>
    public int FilesTotal { get; set; }

    /// <summary>How many have physically reached the trash folder.</summary>
    public int FilesMoved { get; set; }

    /// <summary>
    /// How many the worker gave up on after <see cref="MaxAttemptsPerFile"/> tries.
    ///
    /// <para>Counted rather than fatal, for the reason <c>AccountMigration.FilesFailed</c> gives: one
    /// file Drive will not move must not strand the five thousand behind it. What it costs is
    /// tidiness — the file is still deleted, still in the trash as far as the customer is concerned,
    /// and still destroyed by the purge on its own deadline.</para>
    /// </summary>
    public int FilesFailed { get; set; }

    /// <summary>The last thing Drive said, for whoever looks. Never shown to a customer.</summary>
    public string? FailureReason { get; set; }

    public DeletionJobStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// How many times one file's move is tried before the job moves on without it.
    ///
    /// <para>Three, the same as <c>FileRelocation.MaxAttempts</c> and for the same reason: a file
    /// Drive refuses for a reason that will not change — a shortcut, something the account lost
    /// access to — would otherwise be retried for ever while the rest of the pile waits behind it.
    /// A rate limit is not one of these tries; see <c>DeletionRunner</c>.</para>
    /// </summary>
    public const int MaxAttemptsPerFile = 3;

    /// <summary>Enough for a Drive error and the sentence around it, like every other job row here.</summary>
    public const int MaxFailureReasonLength = 512;
}
