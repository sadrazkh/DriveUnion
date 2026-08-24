using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// The trash is a second surface over the same rows, so it is a second chance to leak them.
///
/// <para>There is no global query filter in this model and there must not be one, so every promise
/// below is a <c>WHERE</c> clause somebody has to have written. These tests are what makes a
/// forgotten one visible.</para>
/// </summary>
public class TrashTenantIsolationTests
{
    [Fact]
    public async Task One_tenant_cannot_see_another_tenants_trash()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var myFile = await harness.SeedUploadedFileAsync(mine, account, name: "mine.mp4", sizeBytes: 1024);
        var theirFile = await harness.SeedUploadedFileAsync(theirs, account, name: "theirs.mp4", sizeBytes: 2048);

        await harness.FilesInTrash().DeleteAsync(mine.Id, myFile.Id, default);
        await harness.FilesInTrash().DeleteAsync(theirs.Id, theirFile.Id, default);

        var listed = await harness.Trash().ListAsync(theirs.Id, default);

        listed.Should().ContainSingle().Which.Id.Should().Be(theirFile.Id);
        (await harness.Trash().SizeAsync(theirs.Id, default)).Should().Be(2048);
    }

    [Fact]
    public async Task One_tenant_cannot_restore_another_tenants_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var myFile = await harness.SeedUploadedFileAsync(mine, account);
        await harness.FilesInTrash().DeleteAsync(mine.Id, myFile.Id, default);

        var trashFolder = harness.FolderOf(myFile);

        (await harness.Trash().RestoreAsync(theirs.Id, myFile.Id, default)).Should().BeFalse();

        // Not merely refused — never attempted. A move asked for on behalf of the wrong tenant would
        // have put somebody else's file back on their behalf.
        harness.Drive.Calls.Count(c => c.Operation == FakeDriveOperation.Move).Should().Be(1);
        harness.FolderOf(myFile).Should().Be(trashFolder);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == myFile.Id);

        row.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Emptying_one_trash_leaves_every_other_tenants_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var myFile = await harness.SeedUploadedFileAsync(mine, account, name: "mine.mp4", sizeBytes: 1024);
        var theirFile = await harness.SeedUploadedFileAsync(theirs, account, name: "theirs.mp4", sizeBytes: 2048);

        await harness.FilesInTrash().DeleteAsync(mine.Id, myFile.Id, default);
        await harness.FilesInTrash().DeleteAsync(theirs.Id, theirFile.Id, default);

        (await harness.Trash().EmptyAsync(theirs.Id, default)).Should().Be(1);

        harness.DriveStillHolds(myFile).Should().BeTrue();
        harness.DriveStillHolds(theirFile).Should().BeFalse();

        // Each counter moved by its own file and nobody else's.
        (await harness.StorageAsync(mine.Id)).Used.Should().Be(1024);
        (await harness.StorageAsync(theirs.Id)).Used.Should().Be(0);

        var rows = await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(myFile.Id);
    }

    [Fact]
    public async Task The_sweeper_releases_each_row_against_the_tenant_the_row_names()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var myFile = await harness.SeedUploadedFileAsync(mine, account, name: "mine.mp4", sizeBytes: 1024);
        var theirFile = await harness.SeedUploadedFileAsync(theirs, account, name: "theirs.mp4", sizeBytes: 2048);

        await harness.FilesInTrash().DeleteAsync(mine.Id, myFile.Id, default);
        await harness.FilesInTrash().DeleteAsync(theirs.Id, theirFile.Id, default);

        harness.Clock.Advance(TimeSpan.FromDays(31));

        // The sweeper has no tenant and cannot be given one: it runs with no request and no
        // principal, and the row is the only place the tenant can come from.
        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(2);

        (await harness.StorageAsync(mine.Id)).Used.Should().Be(0);
        (await harness.StorageAsync(theirs.Id)).Used.Should().Be(0);
    }
}
