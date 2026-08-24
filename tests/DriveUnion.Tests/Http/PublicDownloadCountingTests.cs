using System.Net;
using DriveUnion.Core.Abstractions;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The counter a customer's download cap is spent from, proved through the wire rather than through
/// <c>DownloadCounting</c>.
///
/// The rule itself already has unit tests. What these prove is that the controller applies it to the
/// header the client actually sent, and that the number lands in the database — a controller that
/// forwarded the Range to Drive but counted every request would pass every unit test in the product
/// and still bill twenty downloads for one person scrubbing a video.
/// </summary>
public class PublicDownloadCountingTests
{
    /// <summary>How long a parked transfer may take before this is called a hang rather than waited on.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_request_with_no_range_header_counts_as_one_download()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn11cn11", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(1, "the audit trail has to agree");
    }

    [Fact]
    public async Task A_range_that_starts_at_byte_zero_counts_as_one_download()
    {
        // "bytes=0-" is a client asking for the whole file the polite way. It is a download.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn22cn22", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=0-");

        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task The_one_byte_probe_a_video_element_sends_does_not_count()
    {
        // "bytes=0-0" is how a <video> discovers the length and whether ranges work. The real
        // request follows immediately behind it, so counting the probe bills every playback twice.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn33cn33", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=0-0");

        using var response = await client.SendAsync(request);
        (await response.Content.ReadAsByteArrayAsync()).Should().HaveCount(1);

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(0);
    }

    [Fact]
    public async Task A_mid_file_range_does_not_count()
    {
        // One viewer scrubbing through a video otherwise burns twenty of a customer's five hundred.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn44cn44", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=512-767");

        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
    }

    [Fact]
    public async Task Scrubbing_a_video_costs_one_download_and_not_one_per_seek()
    {
        // The whole rule, end to end, as a player actually behaves: a probe, the opening request,
        // and then a seek for every place the viewer dragged the scrubber to.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn55cn55", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();

        foreach (var range in new[] { "bytes=0-0", "bytes=0-", "bytes=1024-2047", "bytes=2048-3071", "bytes=-512" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
            request.Headers.Add("Range", range);

            using var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();
        }

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task The_landing_page_does_not_count_as_a_download()
    {
        // Opening the card is not taking the file, and a link with a cap of one would otherwise be
        // spent by the page that offers it.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cn66cn66");

        using var client = harness.NewClient();
        await client.GetStringAsync($"/d/{seeded.Slug}");

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
    }

    [Fact]
    public async Task A_cap_of_two_serves_two_files_however_many_transfers_are_open_at_once()
    {
        // The test the old order of operations could not pass.
        //
        // When the counter moved after the last byte, a link's remaining downloads were measured
        // against a number that did not yet include the transfers currently running — so three more
        // visitors arriving while two were in flight were all told yes, and a link capped at two
        // served five files. On a 214 GB file that window is hours wide.
        //
        // Two downloads are opened and held open at Google, each having already reserved its slot and
        // delivered nothing. Three more visitors then ask, and must be refused.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cc77cc77", content: PublicSiteHarness.TestBytes(1024), maxDownloads: 2);

        var drive = new ParkedDriveClient(harness.Drive, parkingSpaces: 2);
        harness.DriveClient = drive;

        using var client = harness.NewClient();
        var url = $"/d/{seeded.Slug}/file";

        var first = client.GetAsync(url);
        await drive.ParkedAsync(1);

        var second = client.GetAsync(url);
        await drive.ParkedAsync(2);

        for (var visitor = 0; visitor < 3; visitor++)
        {
            using var refused = await client.GetAsync(url).WaitAsync(Patience);

            // The same card as revoked, expired and never-existed. Not a 429, not a 409: a refusal
            // that looked different from the others is a fourth way to tell live slugs from dead.
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // One at a time, so the two open transfers finish on their own rather than racing each other
        // through a SQLite connection this suite shares between every request.
        drive.LetGo(1);
        using var servedFirst = await first.WaitAsync(Patience);
        drive.LetGo(2);
        using var servedSecond = await second.WaitAsync(Patience);

        servedFirst.StatusCode.Should().Be(HttpStatusCode.OK);
        servedSecond.StatusCode.Should().Be(HttpStatusCode.OK);
        (await servedFirst.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);
        (await servedSecond.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        drive.Opens.Should().Be(2, "a refused request must never cost the operator a call to Google");
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(2);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(2, "the audit trail has to agree");
    }

    /// <summary>
    /// A Drive that parks each download at the moment it is opened and holds it there until let go.
    ///
    /// The window under test is the one between deciding a link may be served and recording that it
    /// was, so the test has to keep transfers open across other requests — and a fake that blocks
    /// until released is deterministic where a sleep is a guess about how fast the machine is.
    ///
    /// Parking happens before any byte moves, which is where a reservation has to already exist:
    /// nothing has been delivered, and the slot is spent all the same.
    /// </summary>
    private sealed class ParkedDriveClient(IDriveClient inner, int parkingSpaces) : IDriveClient
    {
        private readonly TaskCompletionSource[] arrivals = Gates(parkingSpaces);
        private readonly TaskCompletionSource[] departures = Gates(parkingSpaces);

        private int opens;

        /// <summary>Requests that got as far as Google. A refused one must never appear here.</summary>
        public int Opens => Volatile.Read(ref opens);

        /// <summary>Completes once the nth download (1-based) has reached Google and parked.</summary>
        public Task ParkedAsync(int nth) => arrivals[nth - 1].Task.WaitAsync(Patience);

        public void LetGo(int nth) => departures[nth - 1].TrySetResult();

        public async Task<DriveDownload> OpenDownloadAsync(
            Guid accountId,
            string driveFileId,
            string? rangeHeader,
            CancellationToken cancellationToken)
        {
            var space = Interlocked.Increment(ref opens) - 1;

            if (space < departures.Length)
            {
                arrivals[space].TrySetResult();
                await departures[space].Task;
            }
            else
            {
                // More transfers reached Google than the link's cap allows, which is precisely the
                // failure this test exists to catch. Let everything through instead of parking it:
                // the assertions above say what went wrong, and a deadlocked CI run says nothing.
                foreach (var gate in departures) gate.TrySetResult();
            }

            return await inner.OpenDownloadAsync(accountId, driveFileId, rangeHeader, cancellationToken);
        }

        public Task<DriveResumableSession> BeginResumableUploadAsync(
            Guid accountId,
            DriveUploadRequest request,
            CancellationToken cancellationToken) =>
            inner.BeginResumableUploadAsync(accountId, request, cancellationToken);

        public Task<DriveChunkOutcome> WriteChunkAsync(
            Uri sessionUri,
            Stream content,
            long offset,
            long length,
            long totalSize,
            CancellationToken cancellationToken) =>
            inner.WriteChunkAsync(sessionUri, content, offset, length, totalSize, cancellationToken);

        public Task<long> GetConfirmedLengthAsync(
            Uri sessionUri,
            long totalSize,
            CancellationToken cancellationToken) =>
            inner.GetConfirmedLengthAsync(sessionUri, totalSize, cancellationToken);

        public Task<string> EnsureFolderAsync(
            Guid accountId,
            string folderName,
            string? parentFolderId,
            CancellationToken cancellationToken) =>
            inner.EnsureFolderAsync(accountId, folderName, parentFolderId, cancellationToken);

        public Task MoveAsync(
            Guid accountId,
            string driveFileId,
            string? fromFolderId,
            string toFolderId,
            CancellationToken cancellationToken) =>
            inner.MoveAsync(accountId, driveFileId, fromFolderId, toFolderId, cancellationToken);

        public Task DeleteAsync(Guid accountId, string driveFileId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(accountId, driveFileId, cancellationToken);

        public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken) =>
            inner.GetStorageQuotaAsync(accountId, cancellationToken);

        private static TaskCompletionSource[] Gates(int count) =>
            [.. Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    }
}
