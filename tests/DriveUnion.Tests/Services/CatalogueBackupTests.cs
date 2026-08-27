using System.IO.Compression;
using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Backup;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Writing the catalogue into the pool the catalogue describes.
///
/// <para>Every test here is about one failure: the database is gone, the bytes are all still in
/// Google, and nothing left knows whose they are. So what is asserted is almost never «a method
/// ran» — it is the bytes that actually reached the fake Drive, decompressed and parsed the way a
/// person with no copy of this repository would have to parse them.</para>
///
/// <para>The other half is what must <i>not</i> be in those bytes. The file sits in a Drive account
/// and is treated throughout as something that could leak, so the secrets are seeded on purpose and
/// then hunted for.</para>
/// </summary>
public class CatalogueBackupTests
{
    /// <summary>The snapshot on one account, decompressed — which is all a restore has to do.</summary>
    private static string ReadSnapshot(FakeDriveClient drive, string driveFileId)
    {
        using var raw = new MemoryStream(drive.Files[driveFileId].Content);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        return reader.ReadToEnd();
    }

    /// <summary>Every line as a parsed record, the way <c>jq</c> would see them.</summary>
    private static List<JsonElement> Records(string snapshot) =>
    [
        .. snapshot
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement),
    ];

    private static IEnumerable<JsonElement> OfType(List<JsonElement> records, string type) =>
        records.Where(r => r.GetProperty("type").GetString() == type);

    private static async Task<string> RunAndReadAsync(ServiceTestHarness harness, int chunkSize = CatalogueBackup.DefaultChunkSize)
    {
        (await harness.Backup(chunkSize: chunkSize).RunOnceAsync(default))
            .Should().BeGreaterThan(0, "a healthy pool takes a snapshot");

        var copy = await harness.Db.CatalogueSnapshotCopies.AsNoTracking().FirstAsync();

        return ReadSnapshot(harness.Drive, copy.DriveFileId);
    }

    [Fact]
    public async Task A_file_can_be_found_again_from_the_snapshot_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var acme = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var owner = Guid.NewGuid();

        var file = harness.SeedFile(acme.Id, account.Id, "quarterly.mp4", sizeBytes: 4096, ownerUserId: owner);

        var snapshot = await RunAndReadAsync(harness);
        var records = Records(snapshot);

        var line = OfType(records, "file").Single();

        // The whole point of the feature, asserted field by field: given this line and nothing else,
        // somebody can sign into the right Google account and find the right object.
        line.GetProperty("id").GetGuid().Should().Be(file.Id);
        line.GetProperty("tenantId").GetGuid().Should().Be(acme.Id);
        line.GetProperty("tenantSlug").GetString().Should().Be("acme");
        line.GetProperty("ownerUserId").GetGuid().Should().Be(owner);
        line.GetProperty("accountId").GetGuid().Should().Be(account.Id);
        line.GetProperty("driveFileId").GetString().Should().Be(file.DriveFileId);
        line.GetProperty("name").GetString().Should().Be("quarterly.mp4");
        line.GetProperty("mimeType").GetString().Should().Be("video/mp4");
        line.GetProperty("sizeBytes").GetInt64().Should().Be(4096);
        line.GetProperty("deletedAt").ValueKind.Should().Be(JsonValueKind.Null);

        // And the account line is what turns that opaque id into somewhere to sign in.
        OfType(records, "account").Single()
            .GetProperty("email").GetString().Should().Be(account.Email);
    }

    [Fact]
    public async Task The_first_line_says_what_the_file_is_and_the_last_says_it_is_whole()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        var records = Records(await RunAndReadAsync(harness));

        var header = records[0];
        header.GetProperty("type").GetString().Should().Be("header");
        header.GetProperty("format").GetString().Should().Be(CatalogueSnapshotFormat.FormatId);

        // The header carries its own explanation, because the person reading this file may have no
        // repository to look anything up in — that is the situation it exists for.
        header.GetProperty("note").GetString().Should().NotBeNullOrWhiteSpace();

        // The footer is the only evidence the file is not truncated. A snapshot that stopped halfway
        // looks exactly like a complete one until somebody checks for this line.
        var footer = records[^1];
        footer.GetProperty("type").GetString().Should().Be("footer");
        footer.GetProperty("complete").GetBoolean().Should().BeTrue();
        footer.GetProperty("counts").GetProperty("files").GetInt32().Should().Be(1);
        footer.GetProperty("counts").GetProperty("tenants").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Not_one_secret_in_the_database_reaches_the_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        // Every kind of secret this database holds, made findable on purpose. The snapshot sits in a
        // Drive folder; if any of these can reach it, the backup is a credential leak with a
        // schedule.
        account.RefreshTokenProtected = "REFRESH-TOKEN-MUST-NEVER-BE-BACKED-UP";
        account.AccessTokenProtected = "ACCESS-TOKEN-MUST-NEVER-BE-BACKED-UP";

        harness.Db.ApiTokens.Add(new ApiToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            OwnerUserId = Guid.NewGuid(),
            Name = "ci",
            Prefix = "du_abcde",
            SecretHash = "API-TOKEN-HASH-MUST-NEVER-BE-BACKED-UP",
            CreatedAt = ServiceTestHarness.Now,
        });

        harness.Db.S3Credentials.Add(new S3Credential
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            OwnerUserId = Guid.NewGuid(),
            Name = "backups",
            AccessKeyId = "DUIAEXAMPLEKEYID0000",
            SecretProtected = "S3-SECRET-MUST-NEVER-BE-BACKED-UP",
            CreatedAt = ServiceTestHarness.Now,
        });

        harness.SeedLink(tenant.Id, file.Id, "slugmustnotleak");

        await harness.Db.SaveChangesAsync();

        var snapshot = await RunAndReadAsync(harness);

        snapshot.Should().NotContain("REFRESH-TOKEN-MUST-NEVER-BE-BACKED-UP");
        snapshot.Should().NotContain("ACCESS-TOKEN-MUST-NEVER-BE-BACKED-UP");
        snapshot.Should().NotContain("API-TOKEN-HASH-MUST-NEVER-BE-BACKED-UP");
        snapshot.Should().NotContain("S3-SECRET-MUST-NEVER-BE-BACKED-UP");

        // A share-link slug is not a name, it is the key: anyone holding it downloads the file
        // without signing in. It is deliberately not in the snapshot, unlike the encryption headers,
        // which cannot open anything on their own.
        snapshot.Should().NotContain("slugmustnotleak");

        // The one field of the account that is in it, because without it «account 6f2a…» is not
        // somewhere anybody can sign in.
        snapshot.Should().Contain(account.Email);
    }

    [Fact]
    public async Task An_encrypted_file_keeps_the_header_that_is_the_only_way_to_open_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "secret.bin");

        harness.Db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            SealedBy = SealedBy.Client,
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

        var records = Records(await RunAndReadAsync(harness));
        var header = OfType(records, "encryption").Single();

        // Every field, because a header missing one of them is a file that is unopenable for ever
        // even by the person who has the passphrase — the bytes survive and «encrypted» quietly
        // becomes «destroyed». Restoring the file and not its header would be exactly that.
        header.GetProperty("storedFileId").GetGuid().Should().Be(file.Id);
        header.GetProperty("scheme").GetInt32().Should().Be(1);
        header.GetProperty("segmentSize").GetInt32().Should().Be(1024 * 1024);
        header.GetProperty("noncePrefix").GetString().Should().Be("AAAAAAAAAAA=");
        header.GetProperty("plaintextLength").GetInt64().Should().Be(4096);
        header.GetProperty("kdfSalt").GetString().Should().Be("BBBBBBBBBBBBBBBBBBBBBB==");
        header.GetProperty("kdfIterations").GetInt32().Should().Be(600_000);
        header.GetProperty("wrappedKey").GetString().Should().Be("Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=");
        header.GetProperty("sealedBy").GetString().Should().Be("Client");

        (await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync())
            .EncryptionCount.Should().Be(1, "the count of files that are lost for ever if this is wrong");
    }

    [Fact]
    public async Task A_deleted_file_is_in_it_because_its_bytes_still_are()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "old.mp4", deletedAt: ServiceTestHarness.Now);

        var line = OfType(Records(await RunAndReadAsync(harness)), "file").Single();

        // A row in the trash is a real object still occupying the operator's pool. A snapshot that
        // dropped it would restore a catalogue that does not know the object exists, and nothing
        // would ever purge it.
        line.GetProperty("deletedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        line.GetProperty("name").GetString().Should().Be("old.mp4");
    }

    [Fact]
    public async Task The_customer_s_own_folders_are_in_it_so_a_restore_is_recognisable()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var owner = Guid.NewGuid();

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            OwnerUserId = owner,
            Name = "Q3",
            CreatedAt = ServiceTestHarness.Now,
        };

        harness.Db.Folders.Add(folder);
        await harness.Db.SaveChangesAsync();

        var line = OfType(Records(await RunAndReadAsync(harness)), "folder").Single();

        // Without these a restored workspace is every file in one flat list, which is a library
        // nobody recognises as theirs.
        line.GetProperty("id").GetGuid().Should().Be(folder.Id);
        line.GetProperty("name").GetString().Should().Be("Q3");
        line.GetProperty("parentFolderId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Two_accounts_get_the_same_snapshot_so_losing_one_loses_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var first = harness.SeedAccount();
        var second = harness.SeedAccount();
        harness.SeedFile(tenant.Id, first.Id);

        (await harness.Backup().RunOnceAsync(default)).Should().Be(2);

        var copies = await harness.Db.CatalogueSnapshotCopies.AsNoTracking().ToListAsync();

        copies.Select(c => c.GoogleAccountId).Should().BeEquivalentTo(new[] { first.Id, second.Id });

        // Byte for byte the same, because they are literally the same bytes: one pass over the
        // database feeds every session. Two passes would be two snapshots taken a minute apart and
        // both called the same thing.
        var written = copies.Select(c => harness.Drive.Files[c.DriveFileId].Content).ToList();
        written[0].Should().Equal(written[1]);

        // And each copy is in the account it says it is in — a copy recorded against the wrong
        // account is a copy nobody can find.
        foreach (var copy in copies)
        {
            harness.Drive.Files[copy.DriveFileId].AccountId.Should().Be(copy.GoogleAccountId);
        }
    }

    [Fact]
    public async Task An_account_that_will_not_take_it_does_not_cost_the_snapshot()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();
        harness.SeedAccount();
        harness.SeedFile(tenant.Id, harness.Db.GoogleAccounts.First().Id);

        // The first account refuses to open a session — a dead token, a suspended account, the exact
        // case the second copy exists for.
        harness.Drive.FailNext(
            FakeDriveOperation.BeginResumableUpload,
            new DriveApiException("This account is not accepting uploads."));

        (await harness.Backup().RunOnceAsync(default))
            .Should().Be(1, "one account failing is not the run failing");

        var snapshot = await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync();

        snapshot.Status.Should().Be(CatalogueSnapshotStatus.Completed);
        snapshot.CopiesWanted.Should().Be(2);
        snapshot.CopiesMade.Should().Be(1, "and the screen says so, rather than reporting two");
    }

    [Fact]
    public async Task A_run_with_nowhere_to_write_says_so_instead_of_reporting_success()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount(GoogleAccountStatus.Disconnected);
        harness.SeedFile(tenant.Id, account.Id);

        (await harness.Backup().RunOnceAsync(default)).Should().Be(0);

        var after = await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync();

        // Pending, not Failed: the pool being sick for a minute is worth another go, and the next
        // pass is a minute away rather than a day. What matters is that the reason is on the row,
        // where the operator's screen shows it.
        after.Status.Should().Be(CatalogueSnapshotStatus.Pending);
        after.FailureReason.Should().NotBeNullOrWhiteSpace();

        await harness.Backup().RunOnceAsync(default);
        await harness.Backup().RunOnceAsync(default);

        var exhausted = await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync();

        exhausted.Status.Should().Be(CatalogueSnapshotStatus.Failed);
        exhausted.Attempts.Should().Be(CatalogueSnapshot.MaxAttempts);
        exhausted.CopiesMade.Should().Be(0);
    }

    [Fact]
    public async Task A_copy_Drive_disagrees_about_is_not_recorded_as_a_backup()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        // Drive reports the upload complete and then says something else about the bytes. An
        // unconfirmed backup is worse than a missing one, because it is the one nobody checks.
        harness.Drive.CorruptNextVerification = true;

        (await harness.Backup().RunOnceAsync(default)).Should().Be(0);

        (await harness.Db.CatalogueSnapshotCopies.AsNoTracking().AnyAsync())
            .Should().BeFalse("nothing may be recorded as a copy on the strength of a checksum that did not match");

        (await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync())
            .FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task One_a_day_and_not_one_a_minute()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        (await harness.Backup().RunOnceAsync(default)).Should().BeGreaterThan(0);

        // The worker runs every minute so that a hand-made request is picked up promptly. Without
        // this the pool would fill with a snapshot a minute.
        (await harness.Backup().RunOnceAsync(default)).Should().Be(0);

        harness.Clock.Advance(CatalogueSnapshot.Interval + TimeSpan.FromMinutes(1));

        (await harness.Backup().RunOnceAsync(default)).Should().BeGreaterThan(0);

        (await harness.Db.CatalogueSnapshots.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task An_operator_can_ask_for_one_now_and_pressing_twice_does_not_take_two()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        var who = Guid.NewGuid();

        var first = await harness.Snapshots().RequestAsync(who, default);
        first.Queued.Should().BeTrue();

        // The button a worried operator presses three times. Three snapshots of the same rows
        // minutes apart is three uploads and one answer.
        var second = await harness.Snapshots().RequestAsync(who, default);
        second.Queued.Should().BeFalse();
        second.Refusal.Should().Be(SnapshotRefusal.AlreadyQueued);

        (await harness.Backup().RunOnceAsync(default)).Should().BeGreaterThan(0);

        var written = await harness.Db.CatalogueSnapshots.AsNoTracking().SingleAsync();
        written.ByHand.Should().BeTrue();
        written.RequestedByUserId.Should().Be(who);
        written.Status.Should().Be(CatalogueSnapshotStatus.Completed);
    }

    [Fact]
    public async Task Old_snapshots_leave_the_pool_and_the_record_of_them_stays()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        // One more than the pool keeps, taken a day apart.
        for (var i = 0; i <= CatalogueSnapshot.Keep; i++)
        {
            (await harness.Backup().RunOnceAsync(default)).Should().BeGreaterThan(0);
            harness.Clock.Advance(CatalogueSnapshot.Interval + TimeSpan.FromMinutes(1));
        }

        var oldest = (await harness.Db.CatalogueSnapshots.AsNoTracking().ToListAsync())
            .OrderBy(s => s.RequestedAt)
            .First();

        var doomed = await harness.Db.CatalogueSnapshotCopies.AsNoTracking()
            .SingleAsync(c => c.SnapshotId == oldest.Id);

        harness.Drive.Files.Should().ContainKey(doomed.DriveFileId);

        (await harness.Backup().PruneAsync(default)).Should().Be(1, "one run past the fourteen kept");

        // Gone from the pool — a snapshot a day for ever fills the accounts it is meant to protect.
        harness.Drive.Files.Should().NotContainKey(doomed.DriveFileId);

        // And the row outlives the file. «There was one on A1 in July and it has been rotated out»
        // is a different sentence from «there has never been one», and only one of them is evidence
        // that the backup was running.
        (await harness.Db.CatalogueSnapshotCopies.AsNoTracking().SingleAsync(c => c.Id == doomed.Id))
            .RemovedAt.Should().NotBeNull();

        (await harness.Db.CatalogueSnapshots.AsNoTracking().CountAsync())
            .Should().Be(CatalogueSnapshot.Keep + 1);
    }

    [Fact]
    public async Task A_snapshot_too_big_for_one_chunk_arrives_whole()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        // Enough rows to overflow a chunk several times over. The chunk is shrunk rather than the
        // catalogue grown, because filling 8 MiB of gzip would take a hundred megabytes of rows and
        // a minute of test time — see CatalogueBackup's note on why that parameter exists.
        const int files = 12_000;

        var rows = new List<StoredFile>(files);

        for (var i = 0; i < files; i++)
        {
            rows.Add(new StoredFile
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                GoogleAccountId = account.Id,
                DriveFileId = $"drive-{Guid.NewGuid():N}",
                DriveFolderId = $"folder-{Guid.NewGuid():N}",
                Name = $"report-{i}.pdf",
                MimeType = "application/pdf",
                SizeBytes = 1024 + i,
                CreatedAt = ServiceTestHarness.Now,
                ModifiedAt = ServiceTestHarness.Now,
            });
        }

        harness.Db.StoredFiles.AddRange(rows);
        await harness.Db.SaveChangesAsync();

        var snapshot = await RunAndReadAsync(harness, chunkSize: 256 * 1024);
        var records = Records(snapshot);

        // The fake refuses an out-of-order chunk, a body whose length disagrees with its declared
        // one, and a non-final chunk that is not a multiple of 256 KiB — so getting this far already
        // proves the protocol. What is asserted here is the part a protocol cannot: that the file
        // which came out the other end is complete.
        harness.Drive.Calls.Count(c => c.Operation == FakeDriveOperation.WriteChunk)
            .Should().BeGreaterThan(1, "a snapshot larger than a chunk goes up in pieces");

        OfType(records, "file").Should().HaveCount(files);
        records[^1].GetProperty("type").GetString().Should().Be("footer");
        records[^1].GetProperty("counts").GetProperty("files").GetInt32().Should().Be(files);

        // The last record of the last chunk survived the compaction that slid the buffer down after
        // every send — which is the loop that silently truncates a backup if it is wrong.
        OfType(records, "file").Should().Contain(
            f => f.GetProperty("name").GetString() == $"report-{files - 1}.pdf");
    }

    [Fact]
    public async Task The_snapshot_goes_in_the_operator_s_own_folder_and_no_customer_s()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        harness.SeedFile(tenant.Id, account.Id);

        await harness.Backup().RunOnceAsync(default);

        var copy = await harness.Db.CatalogueSnapshotCopies.AsNoTracking().SingleAsync();

        var folder = harness.Drive.Folders.Single(f => f.Id == copy.DriveFolderId);
        var root = harness.Drive.Folders.Single(f => f.Id == folder.ParentFolderId);

        // Beside the workspace folders and inside none of them. The index of the whole product must
        // not be reachable by anything that walks one customer's tree — a restore, a trash sweep or
        // a drain would all treat it as that customer's own.
        folder.Name.Should().Be(".catalogue");
        root.Name.Should().Be("DriveUnion");
        root.ParentFolderId.Should().BeNull();

        harness.Drive.Folders.Should().NotContain(
            f => f.Name == "acme" && f.Id == copy.DriveFolderId);
    }

    [Fact]
    public async Task The_operator_s_screen_names_the_accounts_that_are_holding_a_copy()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var first = harness.SeedAccount();
        harness.SeedAccount();
        harness.SeedFile(tenant.Id, first.Id);

        (await harness.Snapshots().NewestGoodAtAsync(default))
            .Should().BeNull("nothing has been written yet, and the screen has to be able to say so");

        await harness.Backup().RunOnceAsync(default);

        var recent = await harness.Snapshots().RecentAsync(20, default);
        var view = recent.Single();

        view.Status.Should().Be(CatalogueSnapshotStatus.Completed);
        view.FileCount.Should().Be(1);
        view.SizeBytes.Should().BeGreaterThan(0);

        // Named rather than counted: «two copies» is not something anybody can act on, and «A1, A2»
        // is where to go and sign in.
        view.Copies.Should().HaveCount(2);
        view.Copies.Should().OnlyContain(c => c.IsInThePool && c.AccountEmail.Contains('@'));

        (await harness.Snapshots().NewestGoodAtAsync(default)).Should().Be(ServiceTestHarness.Now);
    }
}
