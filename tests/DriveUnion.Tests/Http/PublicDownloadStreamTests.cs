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

        // Pinned as it is rather than as it ought to be: the counter moves before the first byte is
        // copied, so a transfer that died at 512 of 4096 bytes has still spent one of the
        // customer's downloads. The visitor's Range resume will then spend a second one only if it
        // restarts from zero.
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(1);
    }
}
