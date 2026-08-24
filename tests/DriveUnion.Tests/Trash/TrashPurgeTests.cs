using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Settings;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// Destroying files, which is the only thing in this product that frees space in either direction.
///
/// <para>The order it happens in is the point: Drive first, then the release and the row. A byte
/// gone from Drive and still counted costs the customer an upload; a byte still in Drive and no
/// longer counted costs the operator a pool and is invisible until the pool is full. Every failure
/// here is supposed to land in the first half, where a retry can climb out.</para>
/// </summary>
public class TrashPurgeTests
{
    private static readonly TimeSpan PastTheWindow =
        TimeSpan.FromDays(OperatorSettings.DefaultTrashRetentionDays + 1);

    [Fact]
    public async Task Emptying_the_trash_destroys_the_files_and_gives_the_bytes_back()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var first = await harness.SeedUploadedFileAsync(tenant, account, name: "a.mp4", sizeBytes: 1024);
        var second = await harness.SeedUploadedFileAsync(tenant, account, name: "b.mp4", sizeBytes: 2048);
        var kept = await harness.SeedUploadedFileAsync(tenant, account, name: "c.mp4", sizeBytes: 4096);

        harness.SeedLink(tenant.Id, first.Id, "willgoto");

        await harness.FilesInTrash().DeleteAsync(tenant.Id, first.Id, default);
        await harness.FilesInTrash().DeleteAsync(tenant.Id, second.Id, default);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(7168);

        // Whatever the deadline. This is the button that actually gives the customer their space
        // back, and it is not going to wait a month to do it.
        (await harness.Trash().EmptyAsync(tenant.Id, default)).Should().Be(2);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(4096, "exactly the sum of what went");

        harness.DriveStillHolds(first).Should().BeFalse();
        harness.DriveStillHolds(second).Should().BeFalse();
        harness.DriveStillHolds(kept).Should().BeTrue();

        var rows = await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(kept.Id);

        // The link went with the file it pointed at. There is nothing left for it to resolve to.
        (await harness.NewContext().ShareLinks.AsNoTracking().ToListAsync()).Should().BeEmpty();

        (await harness.Trash().SizeAsync(tenant.Id, default)).Should().Be(0);
    }

    [Fact]
    public async Task Emptying_an_empty_trash_does_nothing_at_all()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        await harness.SeedUploadedFileAsync(tenant, account);

        (await harness.Trash().EmptyAsync(tenant.Id, default)).Should().Be(0);

        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.Delete);
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(1024);
    }

    [Fact]
    public async Task Emptying_takes_a_file_deleted_before_the_trash_existed()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var legacy = harness.SeedFile(tenant.Id, account.Id, "legacy.bin", 700, deletedAt: ServiceTestHarness.Now);
        harness.Drive.SeedFile(account.Id, legacy.DriveFileId, "legacy.bin", "application/octet-stream", [1, 2, 3]);
        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 700, default);

        // The sweeper refuses to guess a deadline for these. The customer pressing empty is the
        // decision it refuses to make, and it is the only thing that ever gives those bytes back.
        (await harness.Trash().EmptyAsync(tenant.Id, default)).Should().Be(1);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
        harness.DriveStillHolds(legacy).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweeper_takes_only_what_is_past_its_deadline()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var due = await harness.SeedUploadedFileAsync(tenant, account, name: "due.mp4", sizeBytes: 1024);
        var later = await harness.SeedUploadedFileAsync(tenant, account, name: "later.mp4", sizeBytes: 2048);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, due.Id, default);

        harness.Clock.Advance(PastTheWindow);

        // Deleted a month after the first one, so its own window has barely started.
        await harness.FilesInTrash().DeleteAsync(tenant.Id, later.Id, default);

        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(1);

        var rows = await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(later.Id);
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(2048);
    }

    [Fact]
    public async Task The_sweeper_leaves_a_row_with_no_deadline_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var legacy = harness.SeedFile(tenant.Id, account.Id, "legacy.bin", 700, deletedAt: ServiceTestHarness.Now);
        harness.Drive.SeedFile(account.Id, legacy.DriveFileId, "legacy.bin", "application/octet-stream", [1, 2, 3]);

        harness.Clock.Advance(TimeSpan.FromDays(3650));

        // Ten years later it is still there, and that is correct: it was deleted under rules that
        // had no retention window in them, and inventing one now destroys somebody's file under a
        // policy that did not exist when they pressed the button.
        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(0);

        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.Delete);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task The_sweeper_takes_no_more_than_the_batch_it_was_given()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        foreach (var index in Enumerable.Range(0, 3))
        {
            var file = await harness.SeedUploadedFileAsync(tenant, account, name: $"file-{index}.mp4");
            await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        }

        harness.Clock.Advance(PastTheWindow);

        // The bound is the whole reason ITrashPurge takes a batch size: a purge deletes in Drive one
        // file at a time against the budget every upload in the product shares.
        (await harness.Sweeper().PurgeDueAsync(2, default)).Should().Be(2);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().ContainSingle();

        (await harness.Sweeper().PurgeDueAsync(2, default)).Should().Be(1);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }

    [Fact]
    public async Task A_drive_delete_that_fails_leaves_the_row_and_the_quota_alone_and_is_tried_again()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        harness.Clock.Advance(PastTheWindow);

        harness.Drive.FailNext(FakeDriveOperation.Delete, new DriveApiException("Drive would not."));

        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(0);

        // Nothing was released and nothing was dropped, because Drive never confirmed the bytes were
        // gone. The counter still reads what the pool still holds.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(4096);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().ContainSingle();
        harness.DriveStillHolds(file).Should().BeTrue();

        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(1);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().BeEmpty();
        harness.DriveStillHolds(file).Should().BeFalse();
    }

    [Fact]
    public async Task One_unpurgeable_file_does_not_stop_the_batch_behind_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        foreach (var index in Enumerable.Range(0, 3))
        {
            var file = await harness.SeedUploadedFileAsync(tenant, account, name: $"file-{index}.mp4");
            await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        }

        harness.Clock.Advance(PastTheWindow);
        harness.Drive.FailNext(FakeDriveOperation.Delete, new DriveApiException("The first one is stuck."));

        // A trash that stops emptying because one file cannot be deleted is a pool that fills up
        // behind it.
        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(2);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_rate_limit_stops_the_sweep_rather_than_spending_the_upload_budget()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        foreach (var index in Enumerable.Range(0, 3))
        {
            var file = await harness.SeedUploadedFileAsync(tenant, account, name: $"file-{index}.mp4");
            await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        }

        harness.Clock.Advance(PastTheWindow);
        harness.Drive.RateLimitNext(FakeDriveOperation.Delete);

        (await harness.Sweeper().PurgeDueAsync(50, default)).Should().Be(0);

        // One attempt, then it stopped. Past a rate limit the housekeeping is spending the requests
        // the customers are uploading with, and what is left is still due five minutes later.
        harness.Drive.Calls.Count(c => c.Operation == FakeDriveOperation.Delete).Should().Be(1);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().HaveCount(3);
    }

    [Fact]
    public async Task The_hosted_sweeper_runs_the_purge_on_its_own()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);

        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        harness.Clock.Advance(PastTheWindow);

        // One purge, watched. The test waits for the pass to finish and only then reads the
        // database: the harness's SQLite connection is shared, and a poll running beside the
        // sweeper's own transaction would be two threads on one connection.
        var watched = new WatchedPurge(harness.Sweeper(harness.NewContext()));

        var services = new ServiceCollection();
        services.AddSingleton<ITrashPurge>(watched);

        await using var provider = services.BuildServiceProvider();

        // A FakeTimeProvider rather than the harness's FixedClock, because this is the one test that
        // depends on the loop's timer and not only on its idea of "now". FixedClock overrides
        // GetUtcNow and nothing else, so Task.Delay against it falls through to the real system timer
        // and would sit here for the full interval.
        var timer = new FakeTimeProvider(harness.Clock.Now);

        var sweeper = new TrashPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new TrashOptions()),
            timer,
            NullLogger<TrashPurgeService>.Instance);

        await sweeper.StartAsync(default);

        try
        {
            // The loop waits an interval before its first pass, and this is what releases it. The
            // wait is there because this service is registered in Program.cs, so every in-process
            // test host runs it — and a background scope against a harness's single shared SQLite
            // connection is a 500 in whatever unrelated request is in flight.
            //
            // Advanced in a loop rather than once, because StartAsync returns as soon as ExecuteAsync
            // yields and the timer is not necessarily registered by then. A single Advance can land
            // before the Delay exists, and the Delay then waits on a clock that never moves again —
            // which is a ten-second timeout and a green-looking mistake about what was proved.
            var interval = TimeSpan.FromSeconds(new TrashOptions().PurgeIntervalSeconds);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

            while (!watched.FirstPass.IsCompleted && DateTimeOffset.UtcNow < deadline)
            {
                timer.Advance(interval);
                await Task.WhenAny(watched.FirstPass, Task.Delay(25));
            }

            await watched.FirstPass.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await sweeper.StopAsync(default);
        }

        watched.Purged.Should().Be(1);
        watched.LargestBatch.Should().Be(new TrashOptions().PurgeBatchSize, "the loop passes the bound on");

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
        (await harness.NewContext().StoredFiles.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    /// <summary>A real purge with a note taken of each pass, so the loop can be waited on.</summary>
    private sealed class WatchedPurge(ITrashPurge inner) : ITrashPurge
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstPass => _first.Task;

        public int Purged { get; private set; }

        public int LargestBatch { get; private set; }

        public async Task<int> PurgeDueAsync(int batchSize, CancellationToken cancellationToken)
        {
            LargestBatch = Math.Max(LargestBatch, batchSize);

            var purged = await inner.PurgeDueAsync(batchSize, cancellationToken);

            Purged += purged;
            _first.TrySetResult();

            return purged;
        }
    }
}
