using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Settings;
using DriveUnion.Core.Storage;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// Deleting more than one thing: a selection of any size, and a folder with everything under it.
///
/// <para>Most of this file is about the split. What the customer can see — the row deleted, the links
/// revoked, the deadline stamped, the folder gone — happens in the request, in a fixed number of
/// statements, whatever the size of the pile. What is left is one Drive move per file that nobody is
/// waiting on, and the tests below are largely about the ways that half can go wrong without costing
/// anybody a file: a move Drive refuses, a move it refuses everybody, and a file the customer takes
/// back before the worker reaches it.</para>
/// </summary>
public class QueuedDeletionTests
{
    /// <summary>
    /// Well past the twenty a request used to be allowed to delete.
    ///
    /// <para>Two hundred was the number in the constant's own comment — «a request that times out
    /// with half the selection deleted and no way to tell which half» — so it is the number this
    /// proves is now one press.</para>
    /// </summary>
    private const int FarPastTheOldCeiling = 200;

    [Fact]
    public async Task A_selection_far_past_the_old_ceiling_is_deleted_in_one_press()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var ids = new List<Guid>();
        for (var i = 0; i < FarPastTheOldCeiling; i++)
        {
            ids.Add((await harness.SeedUploadedFileAsync(tenant, account, name: $"clip-{i}.mp4")).Id);
        }

        var callsBefore = harness.Drive.Calls.Count;

        var result = await harness.Deletions().DeleteFilesAsync(tenant.Id, ids, default);

        result.Files.Should().Be(FarPastTheOldCeiling);

        // The point of the whole design: the press costs no Drive round trips at all. Two hundred of
        // them at a third of a second each is the minute nobody could hold a form post open for.
        harness.Drive.Calls.Skip(callsBefore).Should().BeEmpty();

        var db = harness.NewContext();

        // …and yet every one of them is deleted as far as the customer is concerned, before the
        // response is written.
        (await db.StoredFiles.CountAsync(f => f.TenantId == tenant.Id && f.DeletedAt != null))
            .Should().Be(FarPastTheOldCeiling);

        (await harness.FilesInTrash().ListAsync(tenant.Id, new FileListFilter(), default)).Should().BeEmpty();
        (await harness.Trash().ListAsync(tenant.Id, default)).Should().HaveCount(FarPastTheOldCeiling);

        var job = await db.DeletionJobs.SingleAsync();
        job.Status.Should().Be(DeletionJobStatus.Pending);
        job.FilesTotal.Should().Be(FarPastTheOldCeiling);
        job.Scope.Should().Be(DeletionScope.Selection);

        // Then the half nobody is waiting for.
        (await harness.Deleter().RunOnceAsync(FarPastTheOldCeiling + 1, default))
            .Should().Be(FarPastTheOldCeiling);

        var trashFolder = await harness.TrashFolders().TrashAsync(account.Id, tenant.Id, null, default);

        harness.Drive.Files.Values
            .Where(f => f.AccountId == account.Id)
            .Should().OnlyContain(f => f.ParentFolderId == trashFolder);

        var finished = await harness.NewContext().DeletionJobs.SingleAsync();
        finished.Status.Should().Be(DeletionJobStatus.Completed);
        finished.FilesMoved.Should().Be(FarPastTheOldCeiling);
        finished.FilesFailed.Should().Be(0);
    }

    [Fact]
    public async Task A_folder_takes_everything_under_it_however_deep()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var reports = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var q3 = await harness.Tree().CreateAsync(tenant.Id, user, reports.FolderId, "Q3", default);
        var drafts = await harness.Tree().CreateAsync(tenant.Id, user, q3.FolderId, "Drafts", default);
        var elsewhere = await harness.Tree().CreateAsync(tenant.Id, user, null, "Archive", default);

        var top = await harness.SeedUploadedFileAsync(tenant, account, name: "summary.pdf");
        var middle = await harness.SeedUploadedFileAsync(tenant, account, name: "q3.pdf");
        var deep = await harness.SeedUploadedFileAsync(tenant, account, name: "draft.pdf");
        var safe = await harness.SeedUploadedFileAsync(tenant, account, name: "keep.pdf");

        await harness.Tree().MoveFileAsync(tenant.Id, top.Id, reports.FolderId, default);
        await harness.Tree().MoveFileAsync(tenant.Id, middle.Id, q3.FolderId, default);
        await harness.Tree().MoveFileAsync(tenant.Id, deep.Id, drafts.FolderId, default);
        await harness.Tree().MoveFileAsync(tenant.Id, safe.Id, elsewhere.FolderId, default);

        var result = await harness.Deletions().DeleteFolderAsync(tenant.Id, reports.FolderId!.Value, default);

        result.Found.Should().BeTrue();
        result.Files.Should().Be(3, "the file in Archive is not under this folder");
        result.Folders.Should().Be(3, "the folder, its child and its grandchild");

        // The folder rows go now rather than when the job finishes. A folder left standing while its
        // contents drain is one the customer can still upload into, and this job would then take the
        // file they had just put there.
        var db = harness.NewContext();
        (await db.Folders.Where(f => f.TenantId == tenant.Id).Select(f => f.Name).ToListAsync())
            .Should().Equal("Archive");

        (await db.StoredFiles.SingleAsync(f => f.Id == safe.Id)).DeletedAt.Should().BeNull();

        var job = await db.DeletionJobs.SingleAsync();
        job.Scope.Should().Be(DeletionScope.Folder);

        // The name and not the id: the row it named is gone in the same transaction, so an id here
        // would point at nothing for the whole life of this job.
        job.FolderName.Should().Be("Reports");
    }

    [Fact]
    public async Task An_empty_folder_is_a_name_and_needs_no_worker()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        await harness.Tree().CreateAsync(tenant.Id, user, folder.FolderId, "Q3", default);

        var result = await harness.Deletions().DeleteFolderAsync(tenant.Id, folder.FolderId!.Value, default);

        result.Files.Should().Be(0);
        result.Folders.Should().Be(2);

        // No job, because a worker with nothing to move is a row somebody has to explain — and a
        // «clean-up in progress» line on the screen for a folder that held nothing.
        result.JobId.Should().BeNull();
        (await harness.NewContext().DeletionJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_file_drive_will_not_move_does_not_strand_the_pile()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var fine = await harness.SeedUploadedFileAsync(tenant, account, name: "ok.mp4");

        // A row with no bytes behind it: the fake refuses to move it, the way Drive refuses a file
        // the account has lost access to.
        var stuck = harness.SeedFile(tenant.Id, account.Id, "gone.mp4");

        await harness.Deletions().DeleteFilesAsync(tenant.Id, [fine.Id, stuck.Id], default);

        // Enough passes for three attempts at the stuck one plus the good one and the finish.
        await harness.Deleter().RunOnceAsync(20, default);

        var db = harness.NewContext();
        var job = await db.DeletionJobs.SingleAsync();

        job.Status.Should().Be(DeletionJobStatus.Completed);
        job.FilesMoved.Should().Be(1);
        job.FilesFailed.Should().Be(1);

        var abandoned = await db.StoredFiles.SingleAsync(f => f.Id == stuck.Id);
        abandoned.DeletionAttempts.Should().Be(DeletionJob.MaxAttemptsPerFile);

        // Still deleted, and that is the whole difference between this failure and a lost file: the
        // move is housekeeping inside the operator's own Drive. The customer's row says deleted, the
        // trash lists it, and the purge destroys it on its own deadline whichever folder it is in.
        abandoned.DeletedAt.Should().NotBeNull();
        abandoned.PurgeAfter.Should().NotBeNull();
        (await harness.Trash().ListAsync(tenant.Id, default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_rate_limited_pass_stops_and_costs_the_file_none_of_its_tries()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var first = await harness.SeedUploadedFileAsync(tenant, account, name: "a.mp4");
        await harness.SeedUploadedFileAsync(tenant, account, name: "b.mp4");

        await harness.Deletions().DeleteFilesAsync(tenant.Id, [first.Id], default);

        harness.Drive.FailAlways(
            FakeDriveOperation.Move,
            new DriveRateLimitedException("Slow down.", TimeSpan.FromSeconds(30)));

        (await harness.Deleter().RunOnceAsync(10, default)).Should().Be(0);

        var paused = await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == first.Id);

        // Not this file's fault, so not this file's attempt. Counting it would spend a whole job's
        // three tries on a bad ten minutes at Google and leave the pile in the wrong folder for ever.
        paused.DeletionAttempts.Should().Be(0);
        paused.PendingDeletionJobId.Should().NotBeNull();

        (await harness.NewContext().DeletionJobs.SingleAsync())
            .Status.Should().Be(DeletionJobStatus.Running, "there is still work owed");

        harness.Drive.ClearFailure(FakeDriveOperation.Move);

        (await harness.Deleter().RunOnceAsync(10, default)).Should().Be(1);

        (await harness.NewContext().DeletionJobs.SingleAsync())
            .Status.Should().Be(DeletionJobStatus.Completed);
    }

    [Fact]
    public async Task A_file_taken_back_before_the_worker_reaches_it_is_left_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var file = await harness.SeedUploadedFileAsync(tenant, account, name: "oops.mp4");
        var home = harness.FolderOf(file);

        await harness.Deletions().DeleteFilesAsync(tenant.Id, [file.Id], default);

        // This is what stands in for a cancel, and it is why there is not one: the delete already
        // happened, so what somebody who changed their mind wants is the file back — which the trash
        // has already, per file, and which works before the move as well as after it.
        (await harness.Trash().RestoreAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        // Nothing ever moved it, so putting it back is not a move. Asking Drive to add and remove
        // the same parent in one call is a request whose result depends on the order the far end
        // applies them.
        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.Move);
        harness.FolderOf(file).Should().Be(home);

        (await harness.Deleter().RunOnceAsync(10, default)).Should().Be(0);

        var back = await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == file.Id);

        // The claim is what the worker finds its work by, so clearing it is what makes restore beat
        // a job in flight without either of them knowing about the other. Left set, this file would
        // have been moved into the trash folder some time after the customer had put it back.
        back.PendingDeletionJobId.Should().BeNull();
        back.DeletedAt.Should().BeNull();
        harness.FolderOf(file).Should().Be(home);

        (await harness.NewContext().DeletionJobs.SingleAsync())
            .Status.Should().Be(DeletionJobStatus.Completed, "there was nothing left to find");
    }

    [Fact]
    public async Task The_deadline_is_stamped_when_delete_was_pressed_and_not_when_the_move_lands()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);

        await harness.Deletions().DeleteFilesAsync(tenant.Id, [file.Id], default);

        var expected = ServiceTestHarness.Now.AddDays(OperatorSettings.DefaultTrashRetentionDays);

        (await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == file.Id))
            .PurgeAfter.Should().Be(expected);

        // A pile of forty thousand takes hours to drain. If the worker re-stamped the deadline as it
        // went, the retention window a customer was promised would quietly become «a month from
        // whenever the queue got round to you».
        harness.Clock.Advance(TimeSpan.FromHours(5));

        await harness.Deleter().RunOnceAsync(10, default);

        var moved = await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == file.Id);

        moved.PurgeAfter.Should().Be(expected);
        moved.RestoreFolderId.Should().NotBeNull("the row has to remember where to put it back");
    }

    [Fact]
    public async Task Nothing_comes_off_the_customers_counter_until_the_purge()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        await harness.SeedUploadedFileAsync(tenant, account, name: "a.mp4", sizeBytes: 4096);
        await harness.SeedUploadedFileAsync(tenant, account, name: "b.mp4", sizeBytes: 2048);

        var ids = await harness.NewContext().StoredFiles.Select(f => f.Id).ToListAsync();

        await harness.Deletions().DeleteFilesAsync(tenant.Id, ids, default);
        await harness.Deleter().RunOnceAsync(10, default);

        // The same answer the single-file path gives, and for the same reason: until the purge runs
        // those bytes are genuinely still occupying the operator's pool, so the figure on the
        // customer's screen and the bytes on the disk agree. Queueing the work does not change which
        // moment is the honest one to release at.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(6144);
        (await harness.Trash().SizeAsync(tenant.Id, default)).Should().Be(6144);

        (await harness.Trash().EmptyAsync(tenant.Id, default)).Should().Be(2);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_selection_revokes_its_links_in_the_same_breath()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var doomed = await harness.SeedUploadedFileAsync(tenant, account, name: "shared.pdf");
        var kept = await harness.SeedUploadedFileAsync(tenant, account, name: "kept.pdf");

        harness.SeedLink(tenant.Id, doomed.Id, "goesaway1");
        harness.SeedLink(tenant.Id, kept.Id, "staysput1");

        await harness.Deletions().DeleteFilesAsync(tenant.Id, [doomed.Id], default);

        var db = harness.NewContext();

        // Before the worker has moved anything: leaving them active would let /d/{slug} keep
        // answering for a file the workspace removed, for as long as the queue happened to be.
        (await db.ShareLinks.SingleAsync(l => l.Slug == "goesaway1")).IsActive.Should().BeFalse();
        (await db.ShareLinks.SingleAsync(l => l.Slug == "staysput1")).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task One_workspace_cannot_delete_anothers_files_or_folders()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var yours = await harness.Tree().CreateAsync(theirs.Id, user, null, "Payroll", default);
        var yourFile = await harness.SeedUploadedFileAsync(theirs, account, name: "salaries.xlsx");
        await harness.Tree().MoveFileAsync(theirs.Id, yourFile.Id, yours.FolderId, default);

        // Not matched rather than found and refused: a count of zero says nothing about whether that
        // id exists, which is the whole of the answer somebody probing is allowed to have.
        (await harness.Deletions().DeleteFilesAsync(mine.Id, [yourFile.Id], default))
            .Files.Should().Be(0);

        (await harness.Deletions().DeleteFolderAsync(mine.Id, yours.FolderId!.Value, default))
            .Found.Should().BeFalse();

        var db = harness.NewContext();
        (await db.StoredFiles.SingleAsync(f => f.Id == yourFile.Id)).DeletedAt.Should().BeNull();
        (await db.Folders.CountAsync(f => f.TenantId == theirs.Id)).Should().Be(1);
        (await db.DeletionJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_file_already_in_the_trash_keeps_the_deadline_it_had()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var file = await harness.SeedUploadedFileAsync(tenant, account, name: "old.pdf");
        await harness.Tree().MoveFileAsync(tenant.Id, file.Id, folder.FolderId, default);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);

        var deadline = (await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == file.Id)).PurgeAfter;

        harness.Clock.Advance(TimeSpan.FromDays(3));

        var result = await harness.Deletions().DeleteFolderAsync(tenant.Id, folder.FolderId!.Value, default);

        // It was already on its way out under a deadline of its own, and re-stamping it would push
        // that deadline three days later because somebody tidied the folder it used to be in.
        result.Files.Should().Be(0);
        result.JobId.Should().BeNull();

        (await harness.NewContext().StoredFiles.SingleAsync(f => f.Id == file.Id))
            .PurgeAfter.Should().Be(deadline);
    }

    [Fact]
    public async Task The_screen_is_told_what_is_still_being_tidied_and_stops_being_told_when_it_is_done()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var other = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);

        var first = await harness.SeedUploadedFileAsync(tenant, account, name: "a.pdf");
        var second = await harness.SeedUploadedFileAsync(tenant, account, name: "b.pdf");
        await harness.Tree().MoveFileAsync(tenant.Id, first.Id, folder.FolderId, default);
        await harness.Tree().MoveFileAsync(tenant.Id, second.Id, folder.FolderId, default);

        await harness.Deletions().DeleteFolderAsync(tenant.Id, folder.FolderId!.Value, default);

        var live = await harness.Deletions().LiveAsync(tenant.Id, default);

        live.Should().ContainSingle();
        live[0].FolderName.Should().Be("Reports");
        live[0].Scope.Should().Be(DeletionScope.Folder);
        live[0].Total.Should().Be(2);
        live[0].Done.Should().Be(0);

        // Another workspace's clean-up is not this workspace's business, and the line is drawn on a
        // screen that renders for whoever is signed in.
        (await harness.Deletions().LiveAsync(other.Id, default)).Should().BeEmpty();

        await harness.Deleter().RunOnceAsync(1, default);
        (await harness.Deletions().LiveAsync(tenant.Id, default)).Should().ContainSingle()
            .Which.Done.Should().Be(1);

        await harness.Deleter().RunOnceAsync(10, default);

        // Gone from the screen the moment there is nothing left to say. A line that stayed would be
        // a workspace permanently telling somebody to not wait for something that finished.
        (await harness.Deletions().LiveAsync(tenant.Id, default)).Should().BeEmpty();
    }
}
