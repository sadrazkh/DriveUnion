using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>
/// What one press of delete actually took.
/// </summary>
/// <param name="Found">
/// False only when a folder id is not this workspace's, or is already gone. A selection is always
/// found — ids that do not belong here are simply not matched, and a count is the honest answer.
/// </param>
/// <param name="Files">
/// How many files went to the trash. Already true when this is returned: the rows are stamped and
/// the links are revoked before the call comes back, and only the move inside the operator's Drive
/// is still owed.
/// </param>
/// <param name="Folders">How many folder rows went with it. Zero for a selection.</param>
/// <param name="JobId">
/// The queued clean-up, or null when there was nothing to move — an empty folder is a name, and a
/// name needs no worker.
/// </param>
public sealed record DeletionResult(bool Found, int Files, int Folders, Guid? JobId)
{
    public static DeletionResult NotFound { get; } = new(false, 0, 0, null);
}

/// <summary>One clean-up still running, as the files screen reports it.</summary>
/// <param name="Done">Moved plus given up on: what the worker will not come back to.</param>
public sealed record DeletionJobView(
    Guid Id,
    DeletionScope Scope,
    string? FolderName,
    int Done,
    int Total);

/// <summary>
/// The customer's side of deleting more than one thing.
///
/// <para><c>tenantId</c> is explicit on every call, like everywhere else in this product: there is no
/// global query filter, because <c>/d/{slug}</c> is anonymous and a filter fed by the signed-in user
/// resolves it to nobody.</para>
///
/// <para><b>Nothing here talks to Drive.</b> That is the whole point — see <see cref="DeletionJob"/>
/// for why the visible half of a delete is a handful of statements and the invisible half is a queue.
/// One file at a time is still <see cref="IFileCatalog.DeleteAsync"/>, which does the move inline
/// because one round trip is a round trip somebody can wait for.</para>
/// </summary>
public interface IDeletionQueue
{
    /// <summary>
    /// Sends a selection to the trash, however big it is.
    ///
    /// <para>Ids that are not this workspace's, and files already in the trash, are not matched
    /// rather than refused: the result says how many actually went, which is the number the screen
    /// has to report anyway.</para>
    /// </summary>
    Task<DeletionResult> DeleteFilesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a folder and everything underneath it, however deep and however many.
    ///
    /// <para>The folder rows go in the same transaction as the files, and that is a decision rather
    /// than a convenience: a folder left standing while its contents drain is one the customer can
    /// still upload into, and the job would then take the file they had just put there.</para>
    ///
    /// <para>Files already in the trash are left exactly as they are — they are on their way out
    /// under a deadline of their own, and re-stamping them would move that deadline. What they lose
    /// is the folder they name, which is the case <see cref="ITrash.RestoreAsync"/> already answers
    /// by landing a restore at the root.</para>
    /// </summary>
    Task<DeletionResult> DeleteFolderAsync(
        Guid tenantId,
        Guid folderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The clean-ups this workspace still has running, oldest first.
    ///
    /// <para>Drawn on the files screen so that «the delete is done and the tidying is not» is
    /// something the customer is told rather than something they have to infer. Empty is the normal
    /// state and costs one indexed query.</para>
    /// </summary>
    Task<IReadOnlyList<DeletionJobView>> LiveAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The worker's half: the Drive moves a queued deletion still owes.
///
/// <para>Separate from <see cref="IDeletionQueue"/> because nothing on a screen calls it and it takes
/// as long as Google takes. The hosted service is its only caller in production; the tests call it
/// directly, which is why the loop and the work are different types.</para>
/// </summary>
public interface IDeletionRunner
{
    /// <summary>
    /// Moves at most <paramref name="budget"/> files into the trash folder and reports how many
    /// landed. Zero means there was nothing to do — or that Drive is rate limiting, which stops the
    /// pass rather than burning attempts on files that did nothing wrong.
    /// </summary>
    Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken);
}
