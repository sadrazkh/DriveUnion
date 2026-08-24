using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Settings;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// Where a deleted file went, and when it may be destroyed.
/// </summary>
/// <param name="TrashFolderId">The folder it is in now, which becomes its <c>DriveFolderId</c>.</param>
/// <param name="RestoreFolderId">The folder it came from, which is where a restore puts it back.</param>
/// <param name="PurgeAfter">
/// Stamped from the retention window in force at this moment and never recomputed, so lowering the
/// setting tomorrow cannot reach back and destroy what somebody deleted today expecting a month.
/// </param>
public sealed record TrashPlacement(
    string TrashFolderId,
    string RestoreFolderId,
    DateTimeOffset PurgeAfter);

/// <summary>
/// The trip to the trash: one Drive move, and the two facts the row has to remember to undo it.
///
/// <para>It is a seam rather than three constructor arguments on <c>FileCatalog</c> because the
/// catalogue is otherwise a class that talks to the database and nothing else, and because the
/// harnesses that build one by hand have no Drive to give it.</para>
/// </summary>
public interface ITrashMover
{
    /// <summary>
    /// Moves one file into its owner's trash folder.
    ///
    /// <para><paramref name="currentFolderId"/> is the row's own <c>DriveFolderId</c> and may be
    /// null for a file uploaded before folders were recorded. <see cref="IDriveClient.MoveAsync"/>
    /// reads the real parents from Drive in that case, and the restore folder falls back to the
    /// home <see cref="IDriveFolders"/> resolves for the same owner — which for a row with no owner
    /// is the tenant folder those files were put in, so both layouts come back to where they were.
    /// </para>
    /// </summary>
    Task<TrashPlacement> ToTrashAsync(
        Guid tenantId,
        Guid accountId,
        Guid? ownerUserId,
        string driveFileId,
        string? currentFolderId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrashMover"/>
public sealed class TrashMover(
    IDriveClient drive,
    IDriveFolders folders,
    IOperatorSettingsStore settings,
    TimeProvider clock) : ITrashMover
{
    public async Task<TrashPlacement> ToTrashAsync(
        Guid tenantId,
        Guid accountId,
        Guid? ownerUserId,
        string driveFileId,
        string? currentFolderId,
        CancellationToken cancellationToken)
    {
        // Resolved before the move, because after it the file's parent is the trash and the answer
        // to «where did this come from» would have to be guessed. Both calls are cached by
        // IDriveFolders, and the home is only asked for at all on a row that never recorded one.
        var restoreFolderId = currentFolderId ?? await folders.HomeAsync(
            accountId,
            tenantId,
            ownerUserId,
            cancellationToken);

        var trashFolderId = await folders.TrashAsync(accountId, tenantId, ownerUserId, cancellationToken);

        await drive.MoveAsync(accountId, driveFileId, currentFolderId, trashFolderId, cancellationToken);

        var retentionDays = (await settings.ReadAsync(cancellationToken)).TrashRetentionDays;

        return new TrashPlacement(
            trashFolderId,
            restoreFolderId,
            clock.GetUtcNow().AddDays(retentionDays));
    }
}
