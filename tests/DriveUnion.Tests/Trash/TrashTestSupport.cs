using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Settings;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// The folder layout, resolved against <see cref="FakeDriveClient"/> instead of Google.
///
/// <para>The real <see cref="IDriveFolders"/> is another slice's, being written beside this one, so
/// these tests code against the interface rather than waiting for it. What matters here is only that
/// a home and a trash are two different folders in the same account and that both answers are
/// stable, which is exactly what the delete and restore paths depend on.</para>
/// </summary>
internal sealed class TestDriveFolders(FakeDriveClient drive) : IDriveFolders
{
    public Task<string> HomeAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken) =>
        ResolveAsync(accountId, tenantId, ownerUserId, leaf: null, cancellationToken);

    public Task<string> TrashAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken) =>
        ResolveAsync(accountId, tenantId, ownerUserId, leaf: ".trash", cancellationToken);

    private async Task<string> ResolveAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        string? leaf,
        CancellationToken cancellationToken)
    {
        var root = await drive.EnsureFolderAsync(accountId, "DriveUnion", null, cancellationToken);
        var tenant = await drive.EnsureFolderAsync(accountId, tenantId.ToString("N"), root, cancellationToken);

        // A null owner resolves to the tenant folder, which is where files sat before uploads were
        // separated per person — the rows that predate per-user folders have to keep working.
        var home = ownerUserId is null
            ? tenant
            : await drive.EnsureFolderAsync(accountId, ownerUserId.Value.ToString("N"), tenant, cancellationToken);

        return leaf is null ? home : await drive.EnsureFolderAsync(accountId, leaf, home, cancellationToken);
    }
}

/// <summary>
/// The trash services over <see cref="ServiceTestHarness"/>'s real SQLite database, built by hand
/// the way <see cref="Plans.PlanTestSupport"/> builds the plan services.
///
/// <para>SQLite rather than EF's in-memory provider because most of what is under test is SQL: a
/// conditional release that must not go negative, a delete that has to take a row and its links
/// together, and a sweep whose whole promise is which rows it does <i>not</i> match.</para>
/// </summary>
internal static class TrashTestSupport
{
    /// <summary>
    /// The layout these tests resolve against.
    ///
    /// <para>Named apart from the harness's own <c>Folders</c> on purpose. That one hands back the
    /// real <c>DriveFolders</c> the sibling slice is building, and an instance method quietly wins
    /// over an extension — so sharing the name would have swapped what these tests run against
    /// without a single line of them changing.</para>
    /// </summary>
    public static TestDriveFolders TrashFolders(this ServiceTestHarness harness) => new(harness.Drive);

    public static OperatorSettingsStore Settings(
        this ServiceTestHarness harness,
        DriveUnionDbContext? context = null) =>
        new(context ?? harness.Db, harness.Clock, NullLogger<OperatorSettingsStore>.Instance);

    public static TrashMover Mover(this ServiceTestHarness harness, DriveUnionDbContext? context = null) =>
        new(harness.Drive, harness.TrashFolders(), harness.Settings(context), harness.Clock);

    /// <summary>The catalogue as the application resolves it: with somewhere to put a deleted file.</summary>
    public static FileCatalog FilesInTrash(
        this ServiceTestHarness harness,
        DriveUnionDbContext? context = null) =>
        new(context ?? harness.Db, harness.Clock, harness.Mover(context));

    public static TrashService Trash(this ServiceTestHarness harness, DriveUnionDbContext? context = null) =>
        new(context ?? harness.Db, harness.Drive, NullLogger<TrashService>.Instance);

    public static TrashPurge Sweeper(this ServiceTestHarness harness, DriveUnionDbContext? context = null) =>
        new(context ?? harness.Db, harness.Drive, harness.Clock, NullLogger<TrashPurge>.Instance);

    /// <summary>
    /// A file that exists in three places at once, the way a finished upload leaves it: a row, an
    /// object in the fake Drive sitting in its owner's folder, and bytes on the tenant's counter.
    ///
    /// <para>Without all three, none of the promises here can be tested: a move needs an object, a
    /// release needs a counter that was reserved from, and both need the row to say where to look.
    /// </para>
    /// </summary>
    public static async Task<StoredFile> SeedUploadedFileAsync(
        this ServiceTestHarness harness,
        Tenant tenant,
        GoogleAccount account,
        Guid? ownerUserId = null,
        string name = "quarterly.mp4",
        long sizeBytes = 1024)
    {
        var file = harness.SeedFile(tenant.Id, account.Id, name, sizeBytes);

        var home = await harness.TrashFolders().HomeAsync(account.Id, tenant.Id, ownerUserId, default);

        file.OwnerUserId = ownerUserId;
        file.DriveFolderId = home;
        harness.Db.SaveChanges();

        harness.Drive.SeedFile(account.Id, file.DriveFileId, name, "video/mp4", new byte[16]);
        harness.Drive.Files[file.DriveFileId].ParentFolderId = home;

        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, sizeBytes, default);

        return file;
    }

    /// <summary>Which Drive folder the fake currently holds the file in.</summary>
    public static string? FolderOf(this ServiceTestHarness harness, StoredFile file) =>
        harness.Drive.Files.TryGetValue(file.DriveFileId, out var stored) ? stored.ParentFolderId : null;

    public static bool DriveStillHolds(this ServiceTestHarness harness, StoredFile file) =>
        harness.Drive.Files.ContainsKey(file.DriveFileId);
}
