using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DriveUnion.Core.Abstractions;
using FluentAssertions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The download leg — the part the product is actually sold on.
///
/// The client's <c>Range</c> goes to Drive untouched and Drive's answer comes back untouched. Every
/// byte of interpretation added in between is a way for video seeking to break.
/// </summary>
public class GoogleDriveClientDownloadTests
{
    private const string FileId = "1AbCdEfGhIjKlMnOp";

    [Fact]
    public async Task The_clients_Range_header_is_forwarded_verbatim()
    {
        const string range = "bytes=100-199, 300-399";

        var stub = StubHttpMessageHandler.Always(() => Partial("bytes 100-199/1000", 100));
        var client = DriveClientHarness.Create(stub);

        await using var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, range, CancellationToken.None);

        // Not parsed, not normalised, not re-serialised. Drive owns the semantics of multipart,
        // suffix and open-ended ranges; re-deriving them here would only add a place to be wrong.
        stub.LastRequest.Header("Range").Should().Be(range);
        stub.LastRequest.Uri!.ToString().Should().Contain("alt=media");
        stub.LastRequest.Header("Authorization").Should().Be($"Bearer {StubTokenService.AccessToken}");
    }

    [Fact]
    public async Task A_206_and_its_Content_Range_are_mirrored_back()
    {
        var stub = StubHttpMessageHandler.Always(() => Partial("bytes 100-199/1000", 100));
        var client = DriveClientHarness.Create(stub);

        await using var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, "bytes=100-199", CancellationToken.None);

        download.IsPartial.Should().BeTrue();
        download.ContentRange.Should().Be("bytes 100-199/1000");
        download.ContentLength.Should().Be(100);
        download.ContentType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task A_request_with_no_Range_sends_none()
    {
        var stub = StubHttpMessageHandler.Always(() => Whole("hello"));
        var client = DriveClientHarness.Create(stub);

        await using var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, rangeHeader: null, CancellationToken.None);

        stub.LastRequest.Header("Range").Should().BeNull();
        download.IsPartial.Should().BeFalse();
        download.ContentRange.Should().BeNull();
    }

    [Fact]
    public async Task The_body_is_handed_over_unread()
    {
        var stub = StubHttpMessageHandler.Always(() => Whole("the whole file, notionally 214 GB of it"));
        var client = DriveClientHarness.Create(stub);

        await using var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, null, CancellationToken.None);

        // Nothing in the client may consume this stream: a 214 GB file has to cost a buffer, not a
        // copy, and anything that has already read it has by definition held it.
        using var reader = new StreamReader(download.Content);
        var content = await reader.ReadToEndAsync();

        content.Should().Be("the whole file, notionally 214 GB of it");
    }

    [Fact]
    public async Task Disposing_the_download_disposes_the_response_behind_it()
    {
        var stub = StubHttpMessageHandler.Always(() => Whole("bytes"));
        var client = DriveClientHarness.Create(stub);

        var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, null, CancellationToken.None);

        var stream = download.Content;
        await download.DisposeAsync();

        stream.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_does_not_leave_a_download_half_open()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(
                HttpStatusCode.NotFound,
                """{"error":{"code":404,"message":"File not found","errors":[{"reason":"notFound"}]}}"""));

        var client = DriveClientHarness.Create(stub);

        var act = async () => await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, FileId, null, CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>().WithMessage("*404*");
    }

    [Fact]
    public async Task A_file_id_is_escaped_into_the_path()
    {
        var stub = StubHttpMessageHandler.Always(() => Whole("x"));
        var client = DriveClientHarness.Create(stub);

        await using var download = await client.OpenDownloadAsync(
            DriveClientHarness.AccountId, "a/b?c", null, CancellationToken.None);

        stub.LastRequest.Uri!.OriginalString.Should().Contain("/files/a%2Fb%3Fc?alt=media");
    }

    private static HttpResponseMessage Whole(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        return response;
    }

    private static HttpResponseMessage Partial(string contentRange, int length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(new MemoryStream(new byte[length])),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        return response;
    }
}
