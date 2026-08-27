using System.Text.RegularExpressions;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Uploads;
using FluentAssertions;

namespace DriveUnion.Tests.LocalStorage;

/// <summary>
/// What a customer types must never become a path.
///
/// File names arrive from a browser and end up on <c>/d/{slug}</c>: they are Persian, they contain
/// separators, they contain <c>..</c>, they are <c>CON</c>, they are five thousand characters long.
/// Sanitising such a name is a game nobody wins — <c>..</c> has several spellings once encoding is
/// involved, and two customers who both upload <c>گزارش.pdf</c> must not land on the same bytes. So
/// the backend never consults the name for a location at all, and these tests are how that stays
/// true: every path it produces is a generated identifier, and the display name lives in metadata.
/// </summary>
public class LocalDiskPathSafetyTests
{
    /// <summary>The names a file host actually receives, and the ones an attacker sends.</summary>
    public static readonly string[] HostileNames =
    [
        "../../../../../../etc/passwd",
        @"..\..\..\Windows\System32\drivers\etc\hosts",
        "..",
        ".",
        "CON",
        "aux.txt",
        "NUL.pdf",
        "گزارش سالانه/۱۴۰۵.pdf",
        "فایل\\مهم.pdf",
        "%2e%2e%2fescaped.txt",
        "a\0b.txt",
        "   ",
        "file:with*illegal?chars<>|.txt",
        "~/.ssh/authorized_keys",

        // Longer than any filesystem will accept as a name, in the script the customers use.
        new string('ن', 5000),
    ];

    /// <summary>A generated name: thirty-two hex characters and an extension this backend chose.</summary>
    private static readonly Regex GeneratedFileName = new(
        @"^[0-9a-f]{32}\.(bin|json)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex GeneratedDirectoryName = new(
        @"^([0-9a-f]{32}|accounts|files|sessions)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task A_hostile_file_name_is_stored_verbatim_and_never_written_to_disk_as_one()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        foreach (var name in HostileNames)
        {
            var content = LocalDiskHarness.Content(64, name.Length);
            var metadata = await LocalDiskHarness.UploadAsync(
                client, content, fileName: name, chunkSize: 64);

            // The name survives exactly as the customer typed it — the public page shows it, and
            // Content-Disposition carries it — but it never chose where the bytes went.
            metadata.Name.Should().Be(name);

            await using var download = await client.OpenDownloadAsync(
                LocalDiskHarness.AccountId, metadata.FileId, null, CancellationToken.None);

            (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
        }

        foreach (var file in Directory.GetFiles(harness.Root, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);

            (GeneratedFileName.IsMatch(fileName) || fileName == "folders.json")
                .Should().BeTrue($"'{fileName}' is not a name this backend generates");
        }

        foreach (var directory in Directory.GetDirectories(harness.Root, "*", SearchOption.AllDirectories))
        {
            GeneratedDirectoryName.IsMatch(Path.GetFileName(directory))
                .Should().BeTrue($"'{directory}' is not a directory this backend creates");
        }
    }

    [Fact]
    public async Task Nothing_a_customer_types_escapes_the_storage_root()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        foreach (var name in HostileNames)
        {
            await LocalDiskHarness.UploadAsync(
                client, LocalDiskHarness.Content(64), fileName: name, chunkSize: 64);

            // A blank folder name is refused outright, the way the Google client refuses it; every
            // other shape is recorded and none of them reaches the filesystem.
            if (!string.IsNullOrWhiteSpace(name))
            {
                await client.EnsureFolderAsync(LocalDiskHarness.AccountId, name, null, CancellationToken.None);
            }
        }

        // The root sits alone in a directory of its own. A name that traversed upwards has to appear
        // here first, whatever else it does afterwards.
        Directory.GetFileSystemEntries(harness.Sandbox)
            .Select(Path.GetFileName)
            .Should().Equal("root");
    }

    [Fact]
    public async Task Two_accounts_uploading_the_same_name_do_not_share_a_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var second = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var mine = LocalDiskHarness.Content(256, seed: 1);
        var yours = LocalDiskHarness.Content(256, seed: 2);

        var first = await LocalDiskHarness.UploadAsync(
            client, mine, fileName: "گزارش.pdf", chunkSize: 256);
        var other = await LocalDiskHarness.UploadAsync(
            client, yours, fileName: "گزارش.pdf", chunkSize: 256, accountId: second);

        first.FileId.Should().NotBe(other.FileId);

        await using var served = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, first.FileId, null, CancellationToken.None);

        (await LocalDiskHarness.ReadAllAsync(served)).Should().Equal(mine);
    }

    [Fact]
    public async Task The_same_name_uploaded_twice_is_two_files()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var first = await LocalDiskHarness.UploadAsync(
            client, LocalDiskHarness.Content(256, seed: 3), fileName: "report.pdf", chunkSize: 256);
        var second = await LocalDiskHarness.UploadAsync(
            client, LocalDiskHarness.Content(256, seed: 4), fileName: "report.pdf", chunkSize: 256);

        // Drive does the same: a name is a label, not a key, and the second upload does not silently
        // replace the first customer's file.
        first.FileId.Should().NotBe(second.FileId);
    }

    [Fact]
    public async Task A_folder_is_a_record_rather_than_a_directory()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var root = await client.EnsureFolderAsync(
            LocalDiskHarness.AccountId, "DriveUnion", null, CancellationToken.None);

        var again = await client.EnsureFolderAsync(
            LocalDiskHarness.AccountId, "DriveUnion", null, CancellationToken.None);

        var nested = await client.EnsureFolderAsync(
            LocalDiskHarness.AccountId, "DriveUnion", root, CancellationToken.None);

        root.Should().StartWith("ldf-");
        again.Should().Be(root, "find-or-create is what the Drive client does, so it is what this does");
        nested.Should().NotBe(root, "the same name under a different parent is a different folder");

        Directory.GetDirectories(harness.Root, "DriveUnion", SearchOption.AllDirectories)
            .Should().BeEmpty("a folder name is a customer string and never a directory");
    }

    [Fact]
    public async Task A_folder_named_out_of_the_root_creates_nothing_out_of_the_root()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var id = await client.EnsureFolderAsync(
            LocalDiskHarness.AccountId, "../../escaped", null, CancellationToken.None);

        id.Should().StartWith("ldf-");
        Directory.GetFileSystemEntries(harness.Sandbox).Select(Path.GetFileName).Should().Equal("root");
    }

    [Fact]
    public async Task The_quota_describes_the_volume_the_files_are_on()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var volume = new DriveInfo(Path.GetPathRoot(harness.Root)!);

        var quota = await client.GetStorageQuotaAsync(LocalDiskHarness.AccountId, CancellationToken.None);

        // Honest, and used honestly: the selector subtracts usage from the limit to decide whether an
        // upload fits, and here that difference is the free space on the disk.
        quota.LimitBytes.Should().Be(volume.TotalSize);
        quota.UsageBytes.Should().BeInRange(0, volume.TotalSize);

        var free = quota.LimitBytes - quota.UsageBytes;
        Math.Abs(free - volume.AvailableFreeSpace).Should().BeLessThan(64L * 1024 * 1024);
    }

    [Fact]
    public async Task An_upload_of_a_negative_size_is_not_a_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var nonsense = async () => await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", -2, null),
            CancellationToken.None);

        await nonsense.Should().ThrowAsync<DriveApiException>();
    }

    /// <summary>
    /// The one negative that means something.
    ///
    /// <para><c>UploadChunking.UnknownTotal</c> is «I will tell you how long this is on the chunk
    /// that ends it», which is Drive's own mode for a stream whose length is not knowable in advance
    /// — the catalogue backup gzips a hundred thousand rows and cannot say. This backend has to
    /// behave the way Google's does, or the local-disk substitute quietly refuses a feature that
    /// works in production.</para>
    /// </summary>
    [Fact]
    public async Task An_upload_that_will_name_its_length_later_is_accepted()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest(
                "catalogue.jsonl.gz",
                "application/gzip",
                UploadChunking.UnknownTotal,
                null),
            CancellationToken.None);

        var content = new byte[UploadChunking.DriveChunkMultiple];
        for (var i = 0; i < content.Length; i++) content[i] = (byte)(i % 251);

        // One full-sized chunk that declines to name a total…
        using (var first = new MemoryStream(content, writable: false))
        {
            var outcome = await client.WriteChunkAsync(
                session.SessionUri,
                first,
                0,
                content.Length,
                UploadChunking.UnknownTotal,
                CancellationToken.None);

            outcome.Completed.Should().BeNull("a chunk with no total cannot be the last one");
        }

        // …and a short one that does, which is the only moment the length is decided.
        var total = content.Length + 7;

        using (var last = new MemoryStream(new byte[7], writable: false))
        {
            var outcome = await client.WriteChunkAsync(
                session.SessionUri, last, content.Length, 7, total, CancellationToken.None);

            outcome.Completed.Should().NotBeNull();
            outcome.Completed!.SizeBytes.Should().Be(total);
        }
    }
}
