using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Uploads;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Locking a file that was uploaded in the clear.
///
/// <para><b>Every test here is about the order.</b> The feature is not "encrypt a file" — the
/// sealing loop is <c>RemoteFetcher</c>'s and was already proven — it is "replace a readable copy
/// with an unreadable one without ever being in a state where the customer has neither". So what is
/// asserted is mostly what has <i>not</i> happened: the readable copy is still in Drive after a
/// failed checksum, after a truncated read, after a lost key; and it is gone, and only gone, once
/// the sealed one is verified and the catalogue points at it.</para>
/// </summary>
public class FileLockTests
{
    private static readonly EncryptionHeader Header = new(
        Scheme: 1,
        SegmentSize: Du1.SegmentSize,
        NoncePrefix: Convert.ToBase64String(new byte[8]),
        PlaintextLength: 0,
        KdfSalt: Convert.ToBase64String(new byte[16]),
        KdfIterations: 600_000,
        WrappedKey: Convert.ToBase64String(new byte[60]));

    private static FileLocks Queue(ServiceTestHarness harness, ContentKeyring keyring) =>
        new(harness.Db, keyring, harness.Clock);

    private static FileLocker Runner(ServiceTestHarness harness, ContentKeyring keyring) =>
        new(harness.Db, harness.Drive, new DriveFolders(harness.Db, harness.Drive, harness.FolderCache), keyring,
            harness.Clock, NullLogger<FileLocker>.Instance);

    /// <summary>
    /// <b>The whole feature.</b> The file ends up sealed, the catalogue points at the sealed copy,
    /// and the readable one is gone from Drive — in that order.
    /// </summary>
    [Fact]
    public async Task A_locked_file_keeps_its_identity_and_loses_its_readable_copy()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var content = ServiceTestHarness.Bytes(3000);
        var file = harness.SeedFile(tenant.Id, account.Id, "holiday.mp4", content: content);

        var readableDriveId = file.DriveFileId;

        var started = await Queue(harness, keyring).StartAsync(
            tenant.Id, null, file.Id, Header, new byte[32], default);

        started.Refusal.Should().Be(FileLockRefusal.None);

        (await Runner(harness, keyring).RunOnceAsync(5, default)).Should().Be(1);

        var after = await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);

        // The same row. The customer's file became locked; it was not replaced by a different file
        // that looks like it, so its id, name, folder and tags all survived.
        after.Name.Should().Be("holiday.mp4");
        after.DriveFileId.Should().NotBe(readableDriveId);
        after.SizeBytes.Should().Be(Du1.CipherLength(3000));

        (await harness.Db.FileEncryptions.AsNoTracking().AnyAsync(e => e.StoredFileId == file.Id))
            .Should().BeTrue("a sealed file the catalogue has no header for is one nobody can open");

        // The readable copy is gone, and the sealed one is there.
        harness.Drive.Files.Should().NotContainKey(readableDriveId);
        harness.Drive.Files.Should().ContainKey(after.DriveFileId);
    }

    /// <summary>
    /// <b>The failure this feature exists to not have.</b> A sealed copy Drive disagrees about is
    /// not swapped in, and the readable copy is untouched.
    /// </summary>
    [Fact]
    public async Task A_sealed_copy_that_does_not_verify_costs_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(3000));
        var readableDriveId = file.DriveFileId;

        await Queue(harness, keyring).StartAsync(tenant.Id, null, file.Id, Header, new byte[32], default);

        // Drive answers the verification with a checksum that is not what was sent.
        harness.Drive.CorruptNextVerification = true;

        await Runner(harness, keyring).RunOnceAsync(5, default);

        var after = await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);

        // Still readable, still pointed at, still openable. The customer has lost nothing at all.
        after.DriveFileId.Should().Be(readableDriveId);
        harness.Drive.Files.Should().ContainKey(readableDriveId);

        (await harness.Db.FileEncryptions.AsNoTracking().AnyAsync(e => e.StoredFileId == file.Id))
            .Should().BeFalse("nothing was swapped, so nothing may claim the file is sealed");
    }

    /// <summary>
    /// A job whose swap landed and whose delete did not finishes the delete, and does not seal again.
    ///
    /// <para>This is the crash between steps 3 and 4, and it is the one that leaves a readable copy
    /// in the operator's Drive that nothing points at. The row keeps the Drive id precisely so this
    /// pass can find it.</para>
    /// </summary>
    [Fact]
    public async Task A_lock_that_stopped_after_the_swap_still_deletes_the_readable_copy()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(2000));
        var readableDriveId = file.DriveFileId;

        // A row in exactly the state a process that died between the swap and the delete leaves.
        harness.Db.FileLocks.Add(new FileLock
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            StoredFileId = file.Id,
            Status = FileLockStatus.Running,
            PlaintextLength = 2000,
            GoogleAccountId = account.Id,
            SourceDriveFileId = readableDriveId,
            SealedDriveFileId = "drive-sealed-already",
            KdfSalt = Header.KdfSalt,
            KdfIterations = Header.KdfIterations,
            WrappedKey = Header.WrappedKey,
            NoncePrefix = Header.NoncePrefix,
            CreatedAt = harness.Clock.GetUtcNow(),
        });
        await harness.Db.SaveChangesAsync();

        await Runner(harness, keyring).RunOnceAsync(5, default);

        harness.Drive.Files.Should().NotContainKey(
            readableDriveId,
            "the sealed copy is already in place, so the readable one is what is owed");

        var job = await harness.Db.FileLocks.AsNoTracking().FirstAsync();

        job.SourceRemoved.Should().BeTrue();
        job.Status.Should().Be(FileLockStatus.Completed);

        // And nothing was sealed a second time — the upload path was never entered.
        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.BeginResumableUpload);
    }

    /// <summary>
    /// The key is held in memory and nowhere else, so a restart loses it — and that costs the
    /// customer a passphrase rather than their file.
    /// </summary>
    [Fact]
    public async Task A_lock_whose_key_the_process_no_longer_holds_fails_without_touching_anything()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(2000));
        var readableDriveId = file.DriveFileId;

        var started = await Queue(harness, keyring).StartAsync(
            tenant.Id, null, file.Id, Header, new byte[32], default);

        // What a process restart does.
        keyring.Release(started.LockId!.Value);

        await Runner(harness, keyring).RunOnceAsync(5, default);

        (await harness.Db.FileLocks.AsNoTracking().FirstAsync()).Status
            .Should().Be(FileLockStatus.Failed);

        harness.Drive.Files.Should().ContainKey(readableDriveId);
    }

    /// <summary>
    /// Locking is a copy before it is a replacement, so it is refused when there is no room for both.
    /// </summary>
    [Fact]
    public async Task A_workspace_with_no_room_for_the_second_copy_is_told_so_before_anything_happens()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(3000));

        // Room for what is stored and not a byte more.
        var row = await harness.Db.Tenants.FirstAsync(t => t.Id == tenant.Id);
        row.StorageQuotaBytes = row.StorageUsedBytes;
        await harness.Db.SaveChangesAsync();

        var started = await Queue(harness, keyring).StartAsync(
            tenant.Id, null, file.Id, Header, new byte[32], default);

        started.Refusal.Should().Be(FileLockRefusal.NoRoom);
        started.LockId.Should().BeNull();

        (await harness.Db.FileLocks.AsNoTracking().AnyAsync()).Should().BeFalse();
        keyring.Count.Should().Be(0, "a refusal must not leave a content key in memory");
    }

    [Fact]
    public async Task A_file_that_is_already_locked_or_already_locking_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(1000));

        var first = await Queue(harness, keyring).StartAsync(
            tenant.Id, null, file.Id, Header, new byte[32], default);

        first.Refusal.Should().Be(FileLockRefusal.None);

        // Queued twice would seal twice, and the second job's source id would name a file the first
        // one had already deleted.
        var second = await Queue(harness, keyring).StartAsync(
            tenant.Id, null, file.Id, Header, new byte[32], default);

        second.Refusal.Should().Be(FileLockRefusal.AlreadyLocking);
    }

    /// <summary>
    /// Another workspace's file is not found, which is the same answer an id that never existed gets.
    /// </summary>
    [Fact]
    public async Task A_file_belonging_to_somebody_else_cannot_be_locked()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("other");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(theirs.Id, account.Id, content: ServiceTestHarness.Bytes(1000));

        var started = await Queue(harness, keyring).StartAsync(
            mine.Id, null, file.Id, Header, new byte[32], default);

        started.Refusal.Should().Be(FileLockRefusal.UnknownFile);
    }

    /// <summary>
    /// Every link handed out for the file stops working, rather than turning into a passphrase
    /// prompt for somebody who was never given one.
    /// </summary>
    [Fact]
    public async Task Locking_a_file_revokes_the_links_that_promised_it_readable()
    {
        await using var harness = ServiceTestHarness.Create();
        var keyring = new ContentKeyring();

        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, content: ServiceTestHarness.Bytes(2000));
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        await Queue(harness, keyring).StartAsync(tenant.Id, null, file.Id, Header, new byte[32], default);
        await Runner(harness, keyring).RunOnceAsync(5, default);

        (await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id))
            .IsActive.Should().BeFalse();
    }
}
