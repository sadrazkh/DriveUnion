namespace DriveUnion.Core.Storage;

/// <summary>
/// A file a tenant owns, and the Drive object that physically holds it.
///
/// The panel reads these rows rather than listing Drive live: it is faster, it is already scoped to
/// a tenant, and it keeps the 12,000-queries-per-60-seconds budget for work that actually needs
/// Google. Reconciling drift against Drive is M2's problem, not M1's.
/// </summary>
public sealed class StoredFile
{
    public Guid Id { get; set; }

    /// <summary>Who owns the file.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Where the bytes physically sit. Operator-facing only — never serialised to a tenant.</summary>
    public Guid GoogleAccountId { get; set; }

    public required string DriveFileId { get; set; }

    /// <summary>
    /// Which person uploaded it, and therefore whose folder it sits in.
    ///
    /// <para>Null for every row written before uploads were separated per user. Those files stay in
    /// the tenant folder they were put in — no bytes move and no link breaks — which works because
    /// nothing derives a file's location: <see cref="DriveFolderId"/> is read, not computed.</para>
    /// </summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>
    /// The Drive folder this file is in right now, which is the trash folder while it is deleted.
    ///
    /// <para>Recorded rather than derived, for two reasons. Drive has no move — a file's parents are
    /// a collection and moving means naming the one to remove — so the trip to the trash needs this
    /// value, and asking Drive for it would spend a request to learn something we wrote. And it is
    /// what lets the old tenant-folder layout and the new per-user one coexist without a branch.</para>
    ///
    /// <para>Null on the rows that predate it. The move reads the parents from Drive in that case,
    /// once, and records the answer.</para>
    /// </summary>
    public string? DriveFolderId { get; set; }

    /// <summary>
    /// Where this file goes back to when it is restored: the folder it lived in before deletion.
    /// Null unless it is in the trash.
    /// </summary>
    public string? RestoreFolderId { get; set; }

    /// <summary>
    /// When the purge may take it. Set on deletion from the retention window in force at that
    /// moment, so shortening the window does not retroactively destroy what somebody deleted
    /// yesterday expecting a month.
    ///
    /// <para>Null while the file is live. A row with <see cref="DeletedAt"/> set and this null is a
    /// file deleted before the trash existed, and the sweeper leaves it alone rather than guessing
    /// a deadline for it.</para>
    /// </summary>
    public DateTimeOffset? PurgeAfter { get; set; }

    /// <summary>
    /// Where the customer filed it, or null for the workspace's root.
    ///
    /// <para>Not to be read as a sibling of <see cref="DriveFolderId"/>, which is three lines up and
    /// means something else entirely: that one is a Google Drive folder id and says where the bytes
    /// physically are, and this one is a <see cref="Folder"/> row and says where the customer put
    /// it. They move independently — filing a file somewhere costs no Drive call at all — and the
    /// reasoning is on <see cref="Folder"/>.</para>
    ///
    /// <para>No foreign key, deliberately. Deleting a folder is allowed while files that were in it
    /// sit in the trash, and a cascade would take those files with it while a restrict would refuse
    /// a delete for a reason the customer cannot see. A restore whose folder is gone lands at the
    /// root instead, which is the behaviour that needs no constraint to explain it.</para>
    /// </summary>
    public Guid? FolderId { get; set; }

    public required string Name { get; set; }

    public required string MimeType { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>Soft delete: a revoked link must not resurrect a file the tenant removed.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// The queued deletion that still owes this file its move into the trash folder, or null.
    ///
    /// <para>The queue is a column here rather than a table beside <see cref="DeletionJob"/> on
    /// purpose: claiming five thousand files is then one UPDATE, and a row per file would put the
    /// size of the job back into the request the customer is waiting on — which is the thing the
    /// queue exists to take out of it.</para>
    ///
    /// <para>Non-null means the row already says deleted and the bytes have not physically moved
    /// yet. Nothing a customer can see depends on the difference, and three things depend on the
    /// value: the worker finds its work by it, restoring clears it so a job cannot move a file that
    /// came back, and a value left behind on a finished job is the record of a file Drive would not
    /// move.</para>
    ///
    /// <para>No foreign key to <see cref="DeletionJob"/>. A cascade would take a customer's file with
    /// a job row somebody tidied up, and the same detach hazard that keeps <c>RemoteFetch</c> free of
    /// one applies here — see <c>DriveUnionDbContext</c>.</para>
    /// </summary>
    public Guid? PendingDeletionJobId { get; set; }

    /// <summary>
    /// How many times the worker has tried to move this file into the trash and been refused.
    ///
    /// <para>Zero for every live file and for every file whose move landed. It is on the row rather
    /// than on a per-file job row for the reason above, and it is what stops one file Drive will not
    /// move from being retried for ever in front of the rest of the pile.</para>
    /// </summary>
    public int DeletionAttempts { get; set; }
}
