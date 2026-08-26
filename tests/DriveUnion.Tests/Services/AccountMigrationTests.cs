using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Emptying one pool account into another.
///
/// <para>Almost every test here is about not losing a file. A migration is the only thing in this
/// product that deletes bytes a customer still wants, and it does so on the strength of its own
/// belief that a copy exists somewhere else — so what is under test is mostly the moments where that
/// belief could be wrong, and what happens when it is.</para>
/// </summary>
public class AccountMigrationTests
{
    private static async Task<int> DrainAsync(ServiceTestHarness harness, int budget = 50) =>
        await harness.Migrator().RunOnceAsync(budget, default);

    [Fact]
    public async Task A_file_moves_and_the_catalogue_follows_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        var content = ServiceTestHarness.Bytes(300_000);
        var file = harness.SeedFile(tenant.Id, from.Id, "quarterly.mp4", content: content);

        // Read off now, not after. The harness hands back the tracked entity, which is the very
        // object the migrator mutates — comparing against it later compares the new id with itself.
        var before = file.DriveFileId;

        (await harness.Migrations().StartAsync(from.Id, to.Id, default)).Started.Should().BeTrue();

        await DrainAsync(harness);

        var moved = await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        moved.GoogleAccountId.Should().Be(to.Id);
        moved.DriveFileId.Should().NotBe(before, "it is a new file on a different account");

        // The bytes, not just the row. A migration that updated the catalogue and copied nothing
        // would pass every assertion above it.
        harness.Drive.Files[moved.DriveFileId].Content.Should().Equal(content);
        harness.Drive.Files[moved.DriveFileId].AccountId.Should().Be(to.Id);
    }

    [Fact]
    public async Task The_source_copy_outlives_the_swap_and_is_swept_later()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        var file = harness.SeedFile(
            tenant.Id, from.Id, content: ServiceTestHarness.Bytes(2048));

        var before = file.DriveFileId;

        await harness.Migrations().StartAsync(from.Id, to.Id, default);
        await DrainAsync(harness);

        // Still there. A download that was already streaming from the source when the catalogue
        // swapped is holding an open response, and deleting the file underneath it is a transfer
        // that dies at eighty per cent for no reason the visitor can see.
        harness.Drive.Files.Should().ContainKey(before);

        (await harness.Migrator().SweepMovedSourcesAsync(default))
            .Should().Be(0, "the grace period has not passed");

        harness.Clock.Advance(FileRelocation.Grace + TimeSpan.FromMinutes(1));

        (await harness.Migrator().SweepMovedSourcesAsync(default)).Should().Be(1);

        harness.Drive.Files.Should().NotContainKey(before);

        (await harness.Db.FileRelocations.AsNoTracking().SingleAsync())
            .Status.Should().Be(FileRelocationStatus.SourceRemoved);
    }

    [Fact]
    public async Task A_copy_that_lands_with_the_wrong_bytes_costs_nobody_their_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        var file = harness.SeedFile(
            tenant.Id, from.Id, content: ServiceTestHarness.Bytes(4096));

        var before = file.DriveFileId;

        await harness.Migrations().StartAsync(from.Id, to.Id, default);

        // Drive says the upload finished and then reports something else about it. Contrived, and it
        // is the exact shape of the failure this whole design exists for: the product believes a
        // good copy exists and is about to stop pointing at the only real one.
        harness.Drive.CorruptNextVerification = true;

        // One file, so what is asserted below is the state after the attempt that was lied to. A
        // retry that then succeeds is correct behaviour and is a different question.
        await harness.Migrator().RunOnceAsync(1, default);

        var after = await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        after.GoogleAccountId.Should().Be(from.Id, "the catalogue must not follow a copy that failed");
        after.DriveFileId.Should().Be(before);
        harness.Drive.Files.Should().ContainKey(before);

        (await harness.Db.FileRelocations.AsNoTracking().SingleAsync())
            .MovedAt.Should().BeNull("nothing may be swept for a move that did not happen");
    }

    [Fact]
    public async Task A_file_that_keeps_failing_is_left_behind_rather_than_blocking_the_rest()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        // A row with no bytes behind it: the fake refuses to open it, the way Drive refuses a file
        // the account has lost access to.
        var stuck = harness.SeedFile(tenant.Id, from.Id, "gone.mp4");
        var fine = harness.SeedFile(tenant.Id, from.Id, "ok.mp4", content: ServiceTestHarness.Bytes(512));

        await harness.Migrations().StartAsync(from.Id, to.Id, default);

        // Enough passes for three attempts at the stuck file plus the good one.
        await DrainAsync(harness);

        var relocations = await harness.Db.FileRelocations.AsNoTracking().ToListAsync();

        relocations.Should().Contain(r => r.StoredFileId == stuck.Id
            && r.Status == FileRelocationStatus.Failed
            && r.Attempts == FileRelocation.MaxAttempts);

        // The point: one file Drive will not hand over must not strand the ones behind it.
        (await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == fine.Id))
            .GoogleAccountId.Should().Be(to.Id);

        var migration = await harness.Db.AccountMigrations.AsNoTracking().SingleAsync();
        migration.FilesFailed.Should().Be(1);
        migration.FilesMoved.Should().Be(1);
        migration.Status.Should().Be(AccountMigrationStatus.Completed);
    }

    [Fact]
    public async Task An_encrypted_file_moves_without_the_key_ever_being_needed()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        var ciphertext = ServiceTestHarness.Bytes(4112, seed: 13);
        var file = harness.SeedFile(tenant.Id, from.Id, "secret.bin", content: ciphertext);

        harness.Db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            Scheme = 1,
            SegmentSize = 1024 * 1024,
            NoncePrefix = "AAAAAAAAAAA=",
            PlaintextLength = 4096,
            KdfSalt = "BBBBBBBBBBBBBBBBBBBBBB==",
            KdfIterations = 600_000,
            WrappedKey = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=",
            CreatedAt = ServiceTestHarness.Now,
        });
        await harness.Db.SaveChangesAsync();

        await harness.Migrations().StartAsync(from.Id, to.Id, default);
        await DrainAsync(harness);

        var moved = await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        moved.GoogleAccountId.Should().Be(to.Id);
        harness.Drive.Files[moved.DriveFileId].Content.Should().Equal(ciphertext);

        // Untouched, and it has to be: the header names no account and no Drive id, so where the
        // ciphertext lives was never part of what opens it. Rewriting anything here would be
        // rewriting the file's format because it changed shelves.
        var header = await harness.Db.FileEncryptions.AsNoTracking().SingleAsync();
        header.WrappedKey.Should().Be("Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=");
        header.NoncePrefix.Should().Be("AAAAAAAAAAA=");
        header.PlaintextLength.Should().Be(4096);
    }

    [Fact]
    public async Task Starting_a_drain_stops_new_uploads_landing_on_the_account_being_emptied()
    {
        await using var harness = ServiceTestHarness.Create();
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        await harness.Migrations().StartAsync(from.Id, to.Id, default);

        // Paused, so the upload selector skips it. Without this the drain races the uploads it is
        // trying to get ahead of and never reaches an empty account.
        (await harness.Db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == from.Id))
            .Status.Should().Be(GoogleAccountStatus.Paused);

        (await new SingleAccountUploadTargetSelector(harness.Db).SelectAsync(1024, default))
            .Should().Be(to.Id);
    }

    [Fact]
    public async Task A_file_uploaded_mid_drain_is_picked_up_rather_than_missed()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        harness.SeedFile(tenant.Id, from.Id, "first.mp4", content: ServiceTestHarness.Bytes(256));

        await harness.Migrations().StartAsync(from.Id, to.Id, default);
        await harness.Migrator().RunOnceAsync(1, default);

        // Arrives after the drain has already started. A migration that snapshotted its file list at
        // the beginning would leave this one on the account it was told to empty.
        harness.SeedFile(tenant.Id, from.Id, "later.mp4", content: ServiceTestHarness.Bytes(256, 3));

        await DrainAsync(harness);

        (await harness.Db.StoredFiles.AsNoTracking()
            .CountAsync(f => f.GoogleAccountId == from.Id && f.DeletedAt == null))
            .Should().Be(0);
    }

    [Fact]
    public async Task Deleted_files_are_left_where_they_are()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        var binned = harness.SeedFile(
            tenant.Id, from.Id, "old.mp4", deletedAt: ServiceTestHarness.Now, content: ServiceTestHarness.Bytes(64));

        await harness.Migrations().StartAsync(from.Id, to.Id, default);
        await DrainAsync(harness);

        // It still occupies the account and still costs the operator, and it is on its way out. The
        // purge takes it; spending Google's bandwidth relocating it first would be work for nothing.
        (await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == binned.Id))
            .GoogleAccountId.Should().Be(from.Id);
    }

    [Theory]
    [InlineData(MigrationRefusal.SameAccount)]
    [InlineData(MigrationRefusal.TargetNotHealthy)]
    [InlineData(MigrationRefusal.TargetTooSmall)]
    [InlineData(MigrationRefusal.AlreadyRunning)]
    public async Task A_drain_that_cannot_work_is_refused_before_it_starts(MigrationRefusal expected)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();

        var target = expected switch
        {
            MigrationRefusal.SameAccount => from,
            MigrationRefusal.TargetNotHealthy => harness.SeedAccount(GoogleAccountStatus.Disconnected),
            MigrationRefusal.TargetTooSmall => harness.SeedAccount(quotaTotalBytes: 1000, quotaUsedBytes: 900),
            _ => harness.SeedAccount(),
        };

        if (expected == MigrationRefusal.TargetTooSmall)
        {
            harness.SeedFile(tenant.Id, from.Id, sizeBytes: 5000);
        }

        if (expected == MigrationRefusal.AlreadyRunning)
        {
            (await harness.Migrations().StartAsync(from.Id, target.Id, default)).Started.Should().BeTrue();
        }

        var result = await harness.Migrations().StartAsync(from.Id, target.Id, default);

        // Every one of these is something the operator can see and fix, so it is an answer rather
        // than an exception — and it is refused before anything is paused or queued.
        result.Started.Should().BeFalse();
        result.Refusal.Should().Be(expected);
    }

    [Fact]
    public async Task The_inventory_says_what_each_account_is_holding()
    {
        await using var harness = ServiceTestHarness.Create();
        var acme = harness.SeedTenant("acme");
        var globex = harness.SeedTenant("globex");
        var busy = harness.SeedAccount();
        var empty = harness.SeedAccount();

        harness.SeedFile(acme.Id, busy.Id, "a.mp4", sizeBytes: 1000);
        harness.SeedFile(globex.Id, busy.Id, "b.mp4", sizeBytes: 2000);
        harness.SeedFile(globex.Id, busy.Id, "gone.mp4", sizeBytes: 9000, deletedAt: ServiceTestHarness.Now);

        var inventory = await harness.Migrations().InventoryAsync(default);

        var held = inventory.Single(i => i.AccountId == busy.Id);
        held.FileCount.Should().Be(2);
        held.LiveBytes.Should().Be(3000, "the deleted one is on its way out");

        // Blast radius. Draining an account that serves one customer is a different decision from
        // draining one that serves forty, and nothing else on the screen says which this is.
        held.TenantCount.Should().Be(2);

        inventory.Single(i => i.AccountId == empty.Id).FileCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancelling_keeps_what_has_already_moved()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var from = harness.SeedAccount();
        var to = harness.SeedAccount();

        harness.SeedFile(tenant.Id, from.Id, "a.mp4", content: ServiceTestHarness.Bytes(128));
        harness.SeedFile(tenant.Id, from.Id, "b.mp4", content: ServiceTestHarness.Bytes(128, 5));

        var started = await harness.Migrations().StartAsync(from.Id, to.Id, default);
        await harness.Migrator().RunOnceAsync(1, default);

        (await harness.Migrations().CancelAsync(started.MigrationId!.Value, default)).Should().BeTrue();

        await DrainAsync(harness);

        // Counted rather than named: which of the two goes first is a Guid ordering this test has no
        // opinion about. What matters is that the one that moved stayed moved — putting it back
        // would be a second copy and a second chance to lose it — and that the cancellation stopped
        // the other one where it was.
        (await harness.Db.StoredFiles.AsNoTracking().CountAsync(f => f.GoogleAccountId == to.Id))
            .Should().Be(1);

        (await harness.Db.StoredFiles.AsNoTracking().CountAsync(f => f.GoogleAccountId == from.Id))
            .Should().Be(1);
    }
}
