using DriveUnion.Core.Settings;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// The retention window: read when a file is deleted, written onto that file's own deadline, and
/// never consulted again for it.
///
/// <para>That is the whole promise. Lowering the setting shortens the wait for what is deleted next
/// and cannot reach back and destroy what somebody deleted yesterday expecting a month — which is
/// the one way a settings screen could quietly become a delete button for files nobody chose.</para>
/// </summary>
public class TrashRetentionTests
{
    [Fact]
    public async Task The_seeded_default_is_the_number_drive_and_dropbox_use()
    {
        await using var harness = ServiceTestHarness.Create();

        var settings = await harness.Settings().ReadAsync(default);

        settings.TrashRetentionDays.Should().Be(OperatorSettings.DefaultTrashRetentionDays);
        settings.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_deletion_is_stamped_with_the_window_in_force_at_that_moment()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);

        await harness.Settings().SaveTrashRetentionAsync(7, null, default);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.PurgeAfter.Should().Be(ServiceTestHarness.Now.AddDays(7));
    }

    [Fact]
    public async Task Lowering_the_window_does_not_reach_back_into_the_trash()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);

        await harness.Settings().SaveTrashRetentionAsync(30, null, default);
        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);

        // The operator changes their mind the next day.
        harness.Clock.Advance(TimeSpan.FromDays(1));
        await harness.Settings().SaveTrashRetentionAsync(1, null, default);
        harness.Clock.Advance(TimeSpan.FromDays(1));

        // Two days in, and under the new setting this file would already be gone. Its own deadline
        // is the one that counts, and it was written when it was deleted.
        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(0);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.PurgeAfter.Should().Be(ServiceTestHarness.Now.AddDays(30));
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(4096);

        // And it does apply to the next thing deleted.
        var next = await harness.SeedUploadedFileAsync(tenant, account, name: "next.mp4");
        await harness.FilesInTrash().DeleteAsync(tenant.Id, next.Id, default);

        var nextRow = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == next.Id);

        nextRow.PurgeAfter.Should().Be(harness.Clock.Now.AddDays(1));
    }

    [Fact]
    public async Task A_window_outside_the_range_the_row_declares_is_clamped_rather_than_stored()
    {
        await using var harness = ServiceTestHarness.Create();
        var operatorId = Guid.NewGuid();

        var tooLong = await harness.Settings().SaveTrashRetentionAsync(9999, operatorId, default);

        tooLong.TrashRetentionDays.Should().Be(OperatorSettings.MaximumTrashRetentionDays);
        tooLong.UpdatedByUserId.Should().Be(operatorId);
        tooLong.UpdatedAt.Should().Be(ServiceTestHarness.Now);

        var tooShort = await harness.Settings().SaveTrashRetentionAsync(0, operatorId, default);

        // Below the minimum the trash stops being a safety net and becomes a delay. An operator who
        // wants deletion to be immediate says so with the empty-trash button.
        tooShort.TrashRetentionDays.Should().Be(OperatorSettings.MinimumTrashRetentionDays);

        (await harness.Settings(harness.NewContext()).ReadAsync(default)).TrashRetentionDays
            .Should().Be(OperatorSettings.MinimumTrashRetentionDays);
    }
}
