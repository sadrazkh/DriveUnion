using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>
/// What one pool account is actually holding, for an operator deciding what to do about it.
///
/// <para>Live files only: what is in the trash still occupies the account and still costs the
/// operator, but it is on its way out and moving it would be moving something nobody asked for. The
/// purge takes it; a migration should not.</para>
/// </summary>
/// <param name="TenantCount">
/// How many workspaces have something here. The operator's own number, and the reason it exists is
/// blast radius: draining an account that serves one customer is a different decision from draining
/// one that serves forty.
/// </param>
public sealed record AccountInventory(
    Guid AccountId,
    string Email,
    string Label,
    GoogleAccountStatus Status,
    long QuotaTotalBytes,
    long QuotaUsedBytes,
    int FileCount,
    long LiveBytes,
    int TenantCount)
{
    /// <summary>What Google says is left, or null when nobody has asked Google yet.</summary>
    public long? FreeBytes => QuotaTotalBytes > 0 ? QuotaTotalBytes - QuotaUsedBytes : null;
}

/// <summary>A migration as the operator's screen shows it.</summary>
public sealed record AccountMigrationView(
    Guid Id,
    Guid SourceAccountId,
    string SourceLabel,
    Guid TargetAccountId,
    string TargetLabel,
    AccountMigrationStatus Status,
    int FilesMoved,
    int FilesFailed,
    long BytesMoved,
    int FilesRemaining,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Why a migration was refused before it started. All of these are the operator's to fix.</summary>
public enum MigrationRefusal
{
    None = 0,
    UnknownAccount = 1,
    SameAccount = 2,

    /// <summary>The target is not accepting anything — disconnected, or paused by the operator.</summary>
    TargetNotHealthy = 3,

    /// <summary>Google says the target does not have room for what is on the source.</summary>
    TargetTooSmall = 4,

    /// <summary>One is already running for this source. Two would fight over the same files.</summary>
    AlreadyRunning = 5,
}

public sealed record MigrationStartResult(Guid? MigrationId, MigrationRefusal Refusal)
{
    public bool Started => MigrationId is not null;
}

/// <summary>
/// The operator's view of the pool, and the one thing that can move a file between accounts.
///
/// <para>No tenant anywhere in this interface, deliberately: the pool is the operator's and a
/// customer must never learn it exists, let alone which account holds their file. Everything here
/// is reachable only from the operator panel.</para>
/// </summary>
public interface IAccountMigrations
{
    /// <summary>Every account with what it is holding, for the operator's pool screen.</summary>
    Task<IReadOnlyList<AccountInventory>> InventoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Queues a drain of <paramref name="sourceAccountId"/> into <paramref name="targetAccountId"/>.
    ///
    /// <para>Refuses rather than throwing for everything an operator can see and fix — see
    /// <see cref="MigrationRefusal"/>. Pausing the source is part of accepting: a drain racing new
    /// uploads onto the account it is emptying never finishes.</para>
    /// </summary>
    Task<MigrationStartResult> StartAsync(
        Guid sourceAccountId,
        Guid targetAccountId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AccountMigrationView>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Asks a running migration to stop. What has already moved stays moved.</summary>
    Task<bool> CancelAsync(Guid migrationId, CancellationToken cancellationToken);
}

/// <summary>
/// The worker's half: one file at a time, and the sweep that removes the copies left behind.
///
/// <para>Separate from <see cref="IAccountMigrations"/> because nothing on the operator's screen
/// calls it and it takes as long as a file takes. The hosted service is its only caller in
/// production; the tests call it directly, which is the whole reason a background loop is not
/// where this logic lives.</para>
/// </summary>
public interface IAccountMigrator
{
    /// <summary>
    /// Moves at most <paramref name="budget"/> files of whichever migration is due, and reports how
    /// many it moved. Zero means there was nothing to do.
    /// </summary>
    Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes source copies whose grace period has passed, and reports how many.
    ///
    /// <para>The other half of «verify before deleting». Nothing here can lose a file: every row it
    /// acts on has a verified target copy that the catalogue already points at.</para>
    /// </summary>
    Task<int> SweepMovedSourcesAsync(CancellationToken cancellationToken);
}
