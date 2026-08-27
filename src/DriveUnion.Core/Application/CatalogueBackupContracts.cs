using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>Where one copy of a snapshot is, for the operator's screen.</summary>
/// <param name="AccountLabel">
/// The short handle on the account card — <c>A1</c>, <c>A2</c>. What the operator is actually going
/// to go and open when they need this file.
/// </param>
public sealed record CatalogueSnapshotCopyView(
    Guid AccountId,
    string AccountLabel,
    string AccountEmail,
    string DriveFileId,
    DateTimeOffset WrittenAt,
    DateTimeOffset? RemovedAt)
{
    /// <summary>False once the pruner has taken it. The row stays; the file does not.</summary>
    public bool IsInThePool => RemovedAt is null;
}

/// <summary>One run as the operator's screen shows it.</summary>
public sealed record CatalogueSnapshotView(
    Guid Id,
    string Name,
    CatalogueSnapshotStatus Status,
    bool ByHand,
    DateTimeOffset RequestedAt,
    DateTimeOffset? FinishedAt,
    int TenantCount,
    int AccountCount,
    int FolderCount,
    int FileCount,
    int EncryptionCount,
    long SizeBytes,
    int CopiesWanted,
    int CopiesMade,
    string? FailureReason,
    IReadOnlyList<CatalogueSnapshotCopyView> Copies);

/// <summary>Why a hand-made snapshot was refused. The only one there is, and it is not an error.</summary>
public enum SnapshotRefusal
{
    None = 0,

    /// <summary>
    /// One is already waiting or running. A second would write the same rows to the same accounts
    /// twice for no benefit, and the button is the kind a worried operator presses repeatedly.
    /// </summary>
    AlreadyQueued = 1,
}

public sealed record SnapshotRequestResult(Guid? SnapshotId, SnapshotRefusal Refusal)
{
    public bool Queued => SnapshotId is not null;
}

/// <summary>
/// The operator's view of the catalogue's own backups, and the button that asks for one.
///
/// <para>No tenant appears anywhere in this interface, and it is the one place in the product where
/// that is not simply because the pool is the operator's: a snapshot reads <i>every</i> workspace's
/// rows at once, because the thing being protected is the mapping from all of them to the accounts
/// underneath. It is operator-only for exactly that reason.</para>
/// </summary>
public interface ICatalogueSnapshots
{
    /// <summary>The most recent runs, newest first, with where each copy landed.</summary>
    Task<IReadOnlyList<CatalogueSnapshotView>> RecentAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// When the newest snapshot that actually landed was taken, or null if none ever has.
    ///
    /// <para>Asked separately from <see cref="RecentAsync"/> rather than read off the page it
    /// returns, because the page is the newest runs and a fortnight of nightly failures would push
    /// the last good one off the end of it — turning «your backup has been broken for two weeks»
    /// into «you have never had one», which is a different and less believable sentence.</para>
    /// </summary>
    Task<DateTimeOffset?> NewestGoodAtAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks for a snapshot now rather than at the schedule's next turn.
    ///
    /// <para>It queues a row and returns; the worker writes it within the minute. The alternative —
    /// writing it inside the request — is a form post that holds a connection open for as long as
    /// gzipping a hundred thousand rows and pushing them to Google takes, and that is a page that
    /// times out on exactly the pool big enough to need this.</para>
    /// </summary>
    Task<SnapshotRequestResult> RequestAsync(Guid? requestedByUserId, CancellationToken cancellationToken);
}

/// <summary>
/// The worker's half: writing a snapshot, and removing the ones that are too old to keep.
///
/// <para>Separate from <see cref="ICatalogueSnapshots"/> because nothing on the operator's screen
/// calls it and it takes as long as the pool takes. The hosted service is its only caller in
/// production; the tests call it directly, which is the whole reason a background loop is not where
/// this logic lives.</para>
/// </summary>
public interface ICatalogueBackup
{
    /// <summary>
    /// Writes the snapshot that is due, if one is, and reports how many accounts got a copy.
    ///
    /// <para>Zero means there was nothing to do — not that it failed. A run that fails says so on
    /// its own row, because a number returned to a loop is not somewhere an operator can look.</para>
    /// </summary>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the copies of runs past <see cref="CatalogueSnapshot.Keep"/>, and reports how many
    /// files it removed from the pool.
    ///
    /// <para>What is <i>kept</i> is counted in runs and not in files: a run holds one snapshot
    /// however many accounts have a copy of it, and keeping «fourteen files» would mean keeping a
    /// week of them on a pool of two accounts and a fortnight on a pool of one.</para>
    /// </summary>
    Task<int> PruneAsync(CancellationToken cancellationToken);
}
