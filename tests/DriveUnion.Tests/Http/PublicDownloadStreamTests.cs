using System.Net;
using DriveUnion.Core.Abstractions;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// <c>GET /d/{slug}/file</c> — the bytes, which is what the product is actually sold on.
/// </summary>
public class PublicDownloadStreamTests
{
    [Fact]
    public async Task An_anonymous_request_gets_the_stored_bytes_back_unchanged()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        response.Headers.AcceptRanges.Should().Contain("bytes");

        // The other half of the 206 rule: a request that asked for no range is answered whole, and
        // a Content-Range on a 200 would tell a client its unasked-for range had been honoured.
        response.Content.Headers.ContentRange.Should().BeNull();

        var received = await response.Content.ReadAsByteArrayAsync();
        received.Should().Equal(seeded.Content);

        // One call to Drive, for the file this link points at. Anything else means the controller
        // resolved the wrong row or read the body twice.
        harness.Drive.Calls.Should().ContainSingle()
            .Which.Argument.Should().Be(seeded.DriveFileId);
    }

    [Fact]
    public async Task A_range_request_answers_206_with_a_matching_content_range_and_exactly_those_bytes()
    {
        // Video seeking and a resumed download are the same feature: the client's Range goes to
        // Drive untouched and Drive's own 206 and Content-Range come back untouched.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("rg22rg22", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=100-199");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 100-199/4096");
        response.Content.Headers.ContentLength.Should().Be(100);

        var received = await response.Content.ReadAsByteArrayAsync();
        received.Should().Equal(seeded.Content[100..200]);
    }

    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-4095")]
    public async Task A_range_that_starts_at_byte_zero_is_still_a_206(string range)
    {
        // The commonest range a browser sends, and the one the suite used to miss: a resumed
        // download restarting from the beginning, and a player opening a file. Drive answers every
        // range it can satisfy with a 206 and a Content-Range — including the one whose slice
        // happens to be the whole file — and a 200 here would tell the client its Range was ignored
        // and that partial requests are not on offer.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("zr55zr55", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", range);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 0-4095/4096");
        response.Content.Headers.ContentLength.Should().Be(4096);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        // And it is one download all the same: 206 is about how the bytes travel, not about who
        // pays for them. DownloadCounting reads the first byte of the range, which is zero.
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }

    [Fact]
    public async Task A_suffix_range_answers_206_with_the_tail_of_the_file()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("sf33sf33", content: PublicSiteHarness.TestBytes(4096));

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/d/{seeded.Slug}/file");
        request.Headers.Add("Range", "bytes=-256");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 3840-4095/4096");
        response.Content.Headers.ContentLength.Should().Be(256);

        var received = await response.Content.ReadAsByteArrayAsync();
        received.Should().Equal(seeded.Content[3840..]);
    }

    [Fact]
    public async Task The_stream_says_nothing_about_google_and_never_redirects()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("dd44dd44", content: PublicSiteHarness.TestBytes(1024));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        ((int)response.StatusCode).Should().BeInRange(200, 299);
        response.Headers.Location.Should().BeNull(
            "a redirect to drive.google.com is the one thing this route must never do");

        var headers = string.Join(
            "\n",
            response.Headers
                .Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));

        headers.Should().NotContain("drive.google.com");
        headers.Should().NotContain("googleapis.com");
        headers.Should().NotContain(seeded.DriveFileId);
        headers.Should().NotContain(seeded.GoogleAccountEmail);

        // And the body really is the file rather than an HTML interstitial that names Google.
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);
    }

    [Fact]
    public async Task A_persian_file_name_travels_as_rfc5987_with_an_ascii_fallback()
    {
        // The names in this product are Persian. A raw UTF-8 file name is not representable in a
        // header, so RFC 6266 wants both: filename* for clients that read it, and an ASCII filename
        // for the ones that do not.
        const string persian = "گزارش سالانه.mp4";

        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("fa66fa66", fileName: persian, content: PublicSiteHarness.TestBytes(512));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        var disposition = response.Content.Headers.GetValues("Content-Disposition").Single();
        disposition.Should().StartWith("attachment");

        // A header field value has to be representable on the wire. Raw Persian in a header is the
        // bug this pins.
        disposition.ToCharArray().Should()
            .OnlyContain(c => c < (char)0x80, "an HTTP header field value must be ASCII");

        var parameters = disposition.Split(';', StringSplitOptions.TrimEntries);

        var star = parameters.Single(p => p.StartsWith("filename*=", StringComparison.Ordinal));
        star.Should().StartWith("filename*=UTF-8''");
        Uri.UnescapeDataString(star["filename*=UTF-8''".Length..]).Should().Be(persian);

        var fallback = parameters
            .Single(p => p.StartsWith("filename=", StringComparison.Ordinal))["filename=".Length..]
            .Trim('"');

        fallback.Should().NotBeEmpty(
            "a client that cannot read filename* still has to save the file under some name");
        fallback.Should().EndWith(".mp4", "the extension is the part a naive client actually needs");
    }

    [Fact]
    public async Task A_drive_failure_before_the_first_byte_answers_502_and_bills_nothing()
    {
        // Not the "no longer available" card: the link is fine and storage is not, and telling the
        // visitor otherwise sends them away from a file that will be there in a minute.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("er77er77");

        harness.Drive.FailAlways(FakeDriveOperation.OpenDownload, new DriveApiException("Drive said no."));

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0, "nothing was served");
    }

    [Fact]
    public async Task The_slot_a_failed_transfer_gave_back_is_spendable_by_the_next_visitor()
    {
        // A cap of one, spent on a download that never happened, is a link the customer paid for and
        // nobody can use. The reservation is taken before Google is called and given back when Google
        // refuses, so the visitor a minute later gets the one download the link was sold with.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("rl88rl88", content: PublicSiteHarness.TestBytes(1024), maxDownloads: 1);

        harness.Drive.FailAlways(FakeDriveOperation.OpenDownload, new DriveApiException("Drive said no."));

        using var client = harness.NewClient();

        using var failed = await client.GetAsync($"/d/{seeded.Slug}/file");
        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);

        harness.Drive.ClearFailure(FakeDriveOperation.OpenDownload);

        using var served = await client.GetAsync($"/d/{seeded.Slug}/file");
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(1,
            "the download that did happen is the only one in the audit trail");

        // And the cap still means one: the released slot was given back, not created.
        using var refused = await client.GetAsync($"/d/{seeded.Slug}/file");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_drive_stream_that_dies_mid_response_breaks_the_transfer_rather_than_claiming_success()
    {
        // Spec §9: a stream that dies mid-response cannot change a status code already sent, so the
        // controller aborts the connection and lets the client's Range resume cover it. What must
        // never happen is a short body delivered as though it were the whole file.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("mm88mm88", content: PublicSiteHarness.TestBytes(4096));
        harness.DriveClient = new DyingStreamDriveClient(harness.Drive, bytesBeforeFailure: 512);

        using var client = harness.NewClient();

        using var response = await client.GetAsync(
            $"/d/{seeded.Slug}/file",
            HttpCompletionOption.ResponseHeadersRead);

        // The status line and the promised length left the building before the stream broke; the
        // controller cannot take them back, and it does not pretend otherwise.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(4096);

        byte[]? received = null;
        var failure = await Record.ExceptionAsync(async () =>
            received = await response.Content.ReadAsByteArrayAsync());

        failure.Should().NotBeNull("a truncated transfer must surface as a failure, not as a complete file");
        received.Should().BeNull();

        // And it is free. The stream broke at 512 of 4096 bytes because Google dropped it, which is
        // the operator's failure and not the visitor's; the slot this request reserved before it
        // opened Drive goes back, so the counter reads as though the request never happened and the
        // visitor's Range resume pays for the download it actually completes.
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(0);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(0, "the audit trail has to agree");
    }

    [Fact]
    public async Task A_visitor_who_abandons_their_own_transfer_is_still_charged_for_it()
    {
        // The other half of the rule above, and the half that looks unfair until it is costed: a
        // client that walks away at 99% has already taken the egress. Refunding it makes a 214 GB
        // file free for anyone willing to cancel, again and again, against a cap it never touches.
        //
        // So the two ways a transfer can end short are not the same: Google dropping it releases the
        // slot, the visitor dropping it spends it.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("ab99ab99", content: PublicSiteHarness.TestBytes(4096));
        harness.DriveClient = new AbandonedStreamDriveClient(harness.Drive, bytesBeforeAbandonment: 512);

        using var client = harness.NewClient();

        using var response = await client.GetAsync(
            $"/d/{seeded.Slug}/file",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // What the visitor left with. How a client is told about a body that stopped short is the
        // transport's business — under TestServer the response simply ends — and it is not what this
        // test is about; the number below is.
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content[..512]);

        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
        (await harness.DownloadEventCountAsync(seeded.LinkId)).Should().Be(1, "the audit trail has to agree");
    }

    /// <summary>
    /// A Drive whose body stops with the exception a visitor closing their tab produces.
    ///
    /// <see cref="DyingStreamDriveClient"/> throws an <see cref="IOException"/>, which is Google
    /// failing; this throws an <see cref="OperationCanceledException"/>, which is the request being
    /// aborted under the copy. The controller has to tell those two apart — one releases the
    /// reservation and the other spends it — and nothing else in the suite makes it prove that.
    /// </summary>
    private sealed class AbandonedStreamDriveClient(IDriveClient inner, int bytesBeforeAbandonment)
        : IDriveClient
    {
        public async Task<DriveDownload> OpenDownloadAsync(
            Guid accountId,
            string driveFileId,
            string? rangeHeader,
            CancellationToken cancellationToken)
        {
            var download = await inner.OpenDownloadAsync(accountId, driveFileId, rangeHeader, cancellationToken);

            return new DriveDownload(
                new AbandonedStream(download.Content, bytesBeforeAbandonment),
                download.ContentType,
                download.ContentLength,
                download.ContentRange,
                download.IsPartial,
                download);
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

        public Task<DriveStorageQuota> GetStorageQuotaAsync(
            Guid accountId,
            CancellationToken cancellationToken) =>
            inner.GetStorageQuotaAsync(accountId, cancellationToken);

        private sealed class AbandonedStream(Stream inner, int budget) : Stream
        {
            private bool served;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (served) throw new OperationCanceledException("The visitor closed the tab.");

                served = true;

                return inner.Read(buffer, offset, Math.Min(budget, count));
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (served) throw new OperationCanceledException("The visitor closed the tab.");

                served = true;
                var take = Math.Min(budget, buffer.Length);

                return await inner.ReadAsync(buffer[..take], cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
