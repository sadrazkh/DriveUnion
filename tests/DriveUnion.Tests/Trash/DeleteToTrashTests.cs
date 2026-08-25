using DriveUnion.Core.Settings;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// What pressing delete now does, and — just as important — what it still does not do.
///
/// <para>Before this slice, delete stamped a column and stopped: the bytes stayed in the operator's
/// Drive for ever and the tenant's counter never came down. The fix is not "free it here": it is to
/// move the file somewhere it can be got back from, and to leave the counter alone because until the
/// purge runs those bytes really are still occupying the pool.</para>
/// </summary>
public class DeleteToTrashTests
{
    [Fact]
    public async Task Deleting_moves_the_file_into_the_trash_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);

        var home = harness.FolderOf(file);

        (await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        var trashFolder = await harness.TrashFolders().TrashAsync(account.Id, tenant.Id, null, default);

        trashFolder.Should().NotBe(home, "the trash is a folder beside the home, not the home itself");
        harness.FolderOf(file).Should().Be(trashFolder);

        harness.Drive.Calls.Should().ContainSingle(
            c => c.Operation == FakeDriveOperation.Move && c.Argument == file.DriveFileId);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.DeletedAt.Should().Be(ServiceTestHarness.Now);
        row.DriveFolderId.Should().Be(trashFolder);
        row.RestoreFolderId.Should().Be(home, "the row has to remember where to put it back");
        row.PurgeAfter.Should().Be(
            ServiceTestHarness.Now.AddDays(OperatorSettings.DefaultTrashRetentionDays));
    }

    [Fact]
    public async Task Deleting_frees_nothing_on_the_tenants_counter()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(4096);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);

        // The owner's decision, and the only version where the number is true: the bytes are still
        // in the operator's pool, so the customer's figure still says so. Emptying the trash is what
        // gives them back.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(4096);
        (await harness.Trash().SizeAsync(tenant.Id, default)).Should().Be(4096);
    }

    [Fact]
    public async Task Deleting_still_revokes_the_files_links()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);
        harness.SeedLink(tenant.Id, file.Id, "keepitup");

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);

        var link = await harness.NewContext().ShareLinks.AsNoTracking().SingleAsync(l => l.Slug == "keepitup");

        link.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Restoring_puts_the_file_back_in_the_folder_it_came_from()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var owner = Guid.NewGuid();
        var file = await harness.SeedUploadedFileAsync(tenant, account, ownerUserId: owner, sizeBytes: 2048);

        var home = harness.FolderOf(file);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        harness.FolderOf(file).Should().NotBe(home);

        (await harness.Trash().RestoreAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        harness.FolderOf(file).Should().Be(home);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.DeletedAt.Should().BeNull();
        row.DriveFolderId.Should().Be(home);
        row.RestoreFolderId.Should().BeNull();
        row.PurgeAfter.Should().BeNull();

        (await harness.Trash().SizeAsync(tenant.Id, default)).Should().Be(0);
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(2048, "nothing was ever released");
    }

    [Fact]
    public async Task Restoring_a_file_does_not_bring_its_links_back()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);
        harness.SeedLink(tenant.Id, file.Id, "gonegone");

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        await harness.Trash().RestoreAsync(tenant.Id, file.Id, default);

        var link = await harness.NewContext().ShareLinks.AsNoTracking().SingleAsync(l => l.Slug == "gonegone");

        // Deleting revoked them; restoring is not an un-revoking. /d/{slug} answering again for a
        // link the owner watched die is a surprise nobody asked for, and minting a new one is a
        // press.
        link.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Restoring_something_that_is_not_in_the_trash_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);

        (await harness.Trash().RestoreAsync(tenant.Id, file.Id, default)).Should().BeFalse();
        (await harness.Trash().RestoreAsync(tenant.Id, Guid.NewGuid(), default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_deleted_before_the_trash_existed_restores_where_it_already_is()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        // DeletedAt set, no folder, no deadline: the shape every row deleted under the old code has.
        var file = harness.SeedFile(tenant.Id, account.Id, "legacy.bin", 512, deletedAt: ServiceTestHarness.Now);

        (await harness.Trash().RestoreAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        // Nothing ever moved it, so putting it back is not a move — asking Drive to take a file out
        // of a folder it was never in is the one call here that could fail for no reason.
        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.Move);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_trash_lists_what_is_waiting_and_the_live_catalogue_does_not()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var kept = await harness.SeedUploadedFileAsync(tenant, account, name: "kept.mp4");
        var binned = await harness.SeedUploadedFileAsync(tenant, account, name: "binned.mp4", sizeBytes: 900);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, binned.Id, default);

        var waiting = await harness.Trash().ListAsync(tenant.Id, default);

        waiting.Should().ContainSingle();
        waiting[0].Id.Should().Be(binned.Id);
        waiting[0].Name.Should().Be("binned.mp4");
        waiting[0].SizeBytes.Should().Be(900);
        waiting[0].PurgeAfter.Should().Be(
            ServiceTestHarness.Now.AddDays(OperatorSettings.DefaultTrashRetentionDays));

        var live = await harness.FilesInTrash().ListAsync(tenant.Id, folderId: null, nameQuery: null, default);

        live.Should().ContainSingle().Which.Id.Should().Be(kept.Id);
    }
}
