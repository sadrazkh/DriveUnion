using DriveUnion.Core.Storage;

namespace DriveUnion.Core.Application;

/// <summary>Why a file could not be locked. Every one of these is something the customer can act on.</summary>
public enum FileLockRefusal
{
    None = 0,

    /// <summary>No such file in this workspace, or it is in the trash.</summary>
    UnknownFile = 1,

    /// <summary>It is already locked. Locking it twice would be encrypting ciphertext.</summary>
    AlreadyLocked = 2,

    /// <summary>A lock for this file is already queued or running.</summary>
    AlreadyLocking = 3,

    /// <summary>
    /// The workspace does not have room for the second copy.
    ///
    /// <para>Locking is a copy before it is a replacement: the sealed file has to exist and be
    /// verified before the readable one can be deleted, so for the length of the job the workspace
    /// holds both. Refusing here is the honest answer — the alternative is failing halfway with a
    /// half-written copy and a quota that is already over.</para>
    /// </summary>
    NoRoom = 4,

    /// <summary>The header the browser sent is not shaped like one.</summary>
    MalformedHeader = 5,
}

public sealed record FileLockResult(Guid? LockId, FileLockRefusal Refusal);

/// <summary>One lock, as the file screen shows it.</summary>
public sealed record FileLockView(
    Guid Id,
    Guid StoredFileId,
    string FileName,
    FileLockStatus Status,
    long PlaintextLength,
    long BytesSealed,
    string? FailureReason,
    DateTimeOffset CreatedAt);

/// <summary>
/// Turning a file that is already stored into one the operator cannot read.
///
/// <para><c>tenantId</c> is explicit here as everywhere else in this product. There is no global
/// query filter and there must not be one — <c>/d/{slug}</c> is anonymous and a filter would resolve
/// it to <c>Guid.Empty</c>.</para>
/// </summary>
public interface IFileLocks
{
    /// <summary>
    /// Queues a lock, and takes custody of the content key for as long as it runs.
    /// </summary>
    /// <param name="contentKey">
    /// The raw key, held in memory by <c>ContentKeyring</c> and never written anywhere. It is
    /// separate from <paramref name="header"/> on purpose: that record is what goes in the database,
    /// and this is the thing that must not.
    /// </param>
    /// <param name="header">
    /// What the browser decided. Its <c>PlaintextLength</c> is ignored and replaced with the length
    /// the catalogue already knows — the browser is guessing about a file it has never seen, and
    /// the two disagreeing would produce a header that cannot open its own ciphertext.
    /// </param>
    Task<FileLockResult> StartAsync(
        Guid tenantId,
        Guid? requestedByUserId,
        Guid storedFileId,
        EncryptionHeader header,
        byte[] contentKey,
        CancellationToken cancellationToken);

    /// <summary>What is queued or running for this workspace, newest first.</summary>
    Task<IReadOnlyList<FileLockView>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The work itself, in a class a test can call directly.
///
/// <para>The loop around it is a <c>BackgroundService</c> registered by its own extension method —
/// test hosts share one SQLite connection, and a loop opening its own scope on a timer turns into
/// "database is locked" in the middle of somebody else's transaction.</para>
/// </summary>
public interface IFileLockRunner
{
    /// <summary>
    /// Takes up to <paramref name="most"/> queued locks and carries them out.
    /// </summary>
    /// <returns>How many were finished, successfully or not.</returns>
    Task<int> RunOnceAsync(int most, CancellationToken cancellationToken);
}
