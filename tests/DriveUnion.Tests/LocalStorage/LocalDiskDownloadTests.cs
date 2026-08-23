using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.LocalStorage;
using FluentAssertions;

namespace DriveUnion.Tests.LocalStorage;

/// <summary>
/// The download leg — the part the product is actually sold on.
///
/// Two things are being held in place here. The <c>Range</c> answers have to agree with the ones the
/// real client gets back from Drive, byte for byte, or every test written against this backend is
/// testing something the product does not do. And the body has to stay a file on disk: a 214 GB file
/// must cost this server a buffer, not a copy, and an implementation that quietly returned an array
/// would pass every assertion about content while making the product unshippable.
/// </summary>
public class LocalDiskDownloadTests
{
    private const int Size = 4096;

    [Fact]
    public async Task A_download_with_no_Range_is_the_whole_file_under_a_200()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content, mimeType: "video/mp4");

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, null, CancellationToken.None);

        download.IsPartial.Should().BeFalse();
        download.ContentRange.Should().BeNull();
        download.ContentLength.Should().Be(Size);
        download.ContentType.Should().Be("video/mp4");
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task A_range_in_the_middle_is_206_with_its_bytes_and_its_Content_Range()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=100-199", CancellationToken.None);

        download.IsPartial.Should().BeTrue();
        download.ContentRange.Should().Be("bytes 100-199/4096");
        download.ContentLength.Should().Be(100);
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content[100..200]);
    }

    [Fact]
    public async Task An_open_ended_range_over_the_whole_file_is_still_a_206()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=0-", CancellationToken.None);

        // The commonest request a browser makes. Judging partialness by whether the slice happens to
        // be the whole file would answer 200 here and leave the busiest branch of the download path
        // unproven — and it would disagree with what Drive actually sends back.
        download.IsPartial.Should().BeTrue();
        download.ContentRange.Should().Be("bytes 0-4095/4096");
        download.ContentLength.Should().Be(Size);
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task An_open_ended_range_from_the_middle_runs_to_the_end()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=4000-", CancellationToken.None);

        download.ContentRange.Should().Be("bytes 4000-4095/4096");
        download.ContentLength.Should().Be(96);
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content[4000..]);
    }

    [Fact]
    public async Task A_suffix_range_serves_the_tail()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        // "the last 16 bytes" — how a player finds an MP4's moov atom before it plays anything.
        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=-16", CancellationToken.None);

        download.IsPartial.Should().BeTrue();
        download.ContentRange.Should().Be("bytes 4080-4095/4096");
        download.ContentLength.Should().Be(16);
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content[^16..]);
    }

    [Fact]
    public async Task A_suffix_longer_than_the_file_is_the_whole_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=-999999", CancellationToken.None);

        download.ContentRange.Should().Be("bytes 0-4095/4096");
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task A_range_that_runs_past_the_end_is_clamped_to_it()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=4090-999999", CancellationToken.None);

        download.ContentRange.Should().Be("bytes 4090-4095/4096");
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content[4090..]);
    }

    [Fact]
    public async Task A_range_that_cannot_be_satisfied_fails_loudly()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var metadata = await LocalDiskHarness.UploadAsync(client, LocalDiskHarness.Content(Size));

        var beyond = async () => await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=9000-9100", CancellationToken.None);

        // Drive answers 416 here and a DriveDownload has no shape for one, so the real client throws
        // a DriveApiException as well. Serving something plausible instead would be a silent recovery.
        await beyond.Should().ThrowAsync<DriveApiException>().WithMessage("*416*");
    }

    [Fact]
    public async Task A_Range_header_this_does_not_understand_serves_the_whole_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Size);
        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        // RFC 9110: a Range that cannot be parsed is ignored, and the whole representation is sent.
        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "pages=1-2", CancellationToken.None);

        download.IsPartial.Should().BeFalse();
        download.ContentRange.Should().BeNull();
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task The_body_is_the_file_on_disk_rather_than_a_copy_of_it()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var metadata = await LocalDiskHarness.UploadAsync(client, LocalDiskHarness.Content(Size));

        await using var whole = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, null, CancellationToken.None);

        whole.Content.Should().NotBeAssignableTo<MemoryStream>();
        whole.Content.Should().BeAssignableTo<FileStream>()
            .Which.Name.Should().Be(harness.ContentPath(LocalDiskHarness.AccountId, metadata.FileId));

        await using var slice = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=1024-2047", CancellationToken.None);

        // A range needs the body to stop at the end of the range, which a FileStream on its own
        // cannot do — so it is a window onto the same open file. The evidence that nothing was
        // copied is that the file behind the window is still the whole file.
        var window = slice.Content.Should().BeOfType<FileWindowStream>().Subject;

        window.Length.Should().Be(1024);
        window.Source.Name.Should().Be(harness.ContentPath(LocalDiskHarness.AccountId, metadata.FileId));
        window.Source.Length.Should().Be(Size);
    }

    [Fact]
    public async Task Disposing_the_download_closes_the_file_behind_it()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var metadata = await LocalDiskHarness.UploadAsync(client, LocalDiskHarness.Content(Size));

        var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=0-99", CancellationToken.None);

        var content = download.Content;
        var file = ((FileWindowStream)content).Source;

        await download.DisposeAsync();

        content.CanRead.Should().BeFalse();
        file.CanRead.Should().BeFalse("a handle nobody closes is a file this process holds for ever");
    }

    [Fact]
    public async Task An_unsatisfiable_range_leaves_no_handle_open()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var metadata = await LocalDiskHarness.UploadAsync(client, LocalDiskHarness.Content(Size));
        var path = harness.ContentPath(LocalDiskHarness.AccountId, metadata.FileId);

        var beyond = async () => await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, "bytes=9000-", CancellationToken.None);

        await beyond.Should().ThrowAsync<DriveApiException>();

        // Deleting is the cheapest way to ask Windows whether anything still has the file open.
        var release = () => File.Delete(path);
        release.Should().NotThrow();
    }

    [Fact]
    public async Task A_file_id_that_is_not_ours_is_simply_not_here()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var invented = async () => await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, "../../../../etc/passwd", null, CancellationToken.None);

        // The id never becomes a path: it is parsed back into a GUID first, and this one is not one.
        await invented.Should().ThrowAsync<DriveApiException>().WithMessage("*no file*");
    }

    [Fact]
    public async Task Another_accounts_file_is_not_reachable_through_this_one()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var metadata = await LocalDiskHarness.UploadAsync(client, LocalDiskHarness.Content(Size));

        var elsewhere = async () => await client.OpenDownloadAsync(
            other, metadata.FileId, null, CancellationToken.None);

        // Files are stored per account, the way Drive keeps them per Google account. An id from one
        // account does not resolve against another.
        await elsewhere.Should().ThrowAsync<DriveApiException>();
    }
}
