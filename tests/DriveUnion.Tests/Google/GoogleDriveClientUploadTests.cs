using System.Net;
using System.Text;
using DriveUnion.Core.Abstractions;
using FluentAssertions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The resumable upload leg. Every assertion here is about a header Drive reads literally: get one
/// of them wrong and the session does not fail, it simply stops acknowledging bytes.
/// </summary>
public class GoogleDriveClientUploadTests
{
    private static readonly Uri SessionUri =
        new("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=ABC123");

    [Fact]
    public async Task Opening_a_session_takes_the_uri_from_the_Location_header()
    {
        var stub = StubHttpMessageHandler.Always(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty),
            };
            response.Headers.Location = SessionUri;
            return response;
        });

        var time = new ImmediateTimeProvider();
        var client = DriveClientHarness.Create(stub, time);

        var session = await client.BeginResumableUploadAsync(
            DriveClientHarness.AccountId,
            new DriveUploadRequest("movie.mkv", "video/x-matroska", 1048576, "parent-folder-id"),
            CancellationToken.None);

        session.SessionUri.Should().Be(SessionUri);

        // About a week, and Google's number rather than ours — the row this feeds only decides when
        // we stop offering to resume, not when Drive stops allowing it.
        session.ExpiresAt.Should().Be(time.GetUtcNow().AddDays(7));

        var request = stub.LastRequest;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.Query.Should().Contain("uploadType=resumable");
        request.Header("Authorization").Should().Be($"Bearer {StubTokenService.AccessToken}");
        request.Header("X-Upload-Content-Length").Should().Be("1048576");

        var body = Encoding.UTF8.GetString(request.Body);
        body.Should().Contain("\"name\":\"movie.mkv\"");
        body.Should().Contain("\"mimeType\":\"video/x-matroska\"");
        body.Should().Contain("\"parents\":[\"parent-folder-id\"]");
    }

    [Fact]
    public async Task A_session_opened_without_a_Location_header_is_refused()
    {
        var stub = StubHttpMessageHandler.Always(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var client = DriveClientHarness.Create(stub);

        var act = async () => await client.BeginResumableUploadAsync(
            DriveClientHarness.AccountId,
            new DriveUploadRequest("movie.mkv", "video/x-matroska", 1024, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>().WithMessage("*Location*");
    }

    [Fact]
    public async Task A_308_reports_the_prefix_Google_actually_confirmed()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete("bytes=0-262143"));
        var client = DriveClientHarness.Create(stub);

        using var content = new MemoryStream(new byte[262144]);

        var outcome = await client.WriteChunkAsync(
            SessionUri,
            content,
            offset: 0,
            length: 262144,
            totalSize: 1048576,
            CancellationToken.None);

        // bytes=0-262143 is an inclusive range, so the confirmed length is one more than the end.
        outcome.ConfirmedLength.Should().Be(262144);
        outcome.Completed.Should().BeNull();

        var request = stub.LastRequest;
        request.Method.Should().Be(HttpMethod.Put);
        request.ContentHeader("Content-Range").Should().Be("bytes 0-262143/1048576");
        request.Body.Length.Should().Be(262144);
    }

    [Fact]
    public async Task A_308_with_no_Range_header_means_Google_is_holding_nothing()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete(null));
        var client = DriveClientHarness.Create(stub);

        using var content = new MemoryStream(new byte[262144]);

        var outcome = await client.WriteChunkAsync(
            SessionUri, content, 0, 262144, 1048576, CancellationToken.None);

        outcome.ConfirmedLength.Should().Be(0);
    }

    [Fact]
    public async Task A_chunk_write_is_marked_as_something_that_must_never_be_replayed()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete("bytes=0-262143"));
        var client = DriveClientHarness.Create(stub);

        using var content = new MemoryStream(new byte[262144]);

        await client.WriteChunkAsync(SessionUri, content, 0, 262144, 1048576, CancellationToken.None);

        stub.LastRequest.MarkedNonRewindable.Should().BeTrue(
            "the stream has already been drained into the first attempt, so a retry would send "
            + "nothing at all under a Content-Range that still claims the full chunk");
    }

    [Fact]
    public async Task The_caller_keeps_ownership_of_the_chunk_stream()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete("bytes=0-262143"));
        var client = DriveClientHarness.Create(stub);

        using var content = new MemoryStream(new byte[262144]);

        await client.WriteChunkAsync(SessionUri, content, 0, 262144, 1048576, CancellationToken.None);

        // The stream handed in is the ASP.NET request body. Closing it here would tear down a
        // pipeline that is still mid-request.
        content.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task The_chunk_that_finishes_the_file_carries_its_metadata()
    {
        const string metadata = """
            {
              "id": "1AbCdEfGhIjKlMnOp",
              "name": "movie.mkv",
              "mimeType": "video/x-matroska",
              "size": "1048576",
              "createdTime": "2026-08-23T09:15:00.123Z",
              "modifiedTime": "2026-08-23T09:16:00.000Z"
            }
            """;

        var stub = StubHttpMessageHandler.Always(() => StubResponses.Json(HttpStatusCode.OK, metadata));
        var client = DriveClientHarness.Create(stub);

        using var content = new MemoryStream(new byte[1024]);

        var outcome = await client.WriteChunkAsync(
            SessionUri, content, offset: 1047552, length: 1024, totalSize: 1048576, CancellationToken.None);

        outcome.ConfirmedLength.Should().Be(1048576);
        outcome.Completed.Should().NotBeNull();
        outcome.Completed!.FileId.Should().Be("1AbCdEfGhIjKlMnOp");
        outcome.Completed.Name.Should().Be("movie.mkv");
        outcome.Completed.MimeType.Should().Be("video/x-matroska");

        // Drive sends 64-bit values as JSON strings.
        outcome.Completed.SizeBytes.Should().Be(1048576);
        outcome.Completed.CreatedTime.Should().Be(
            new DateTimeOffset(2026, 8, 23, 9, 15, 0, 123, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_completion_without_a_file_id_is_not_a_completion()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"name":"movie.mkv"}"""));

        var client = DriveClientHarness.Create(stub);
        using var content = new MemoryStream(new byte[1024]);

        var act = async () => await client.WriteChunkAsync(
            SessionUri, content, 1047552, 1024, 1048576, CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>().WithMessage("*file id*");
    }

    [Fact]
    public async Task A_dead_session_says_so_by_name()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.NotFound, """{"error":{"code":404}}"""));

        var client = DriveClientHarness.Create(stub);
        using var content = new MemoryStream(new byte[262144]);

        var act = async () => await client.WriteChunkAsync(
            SessionUri, content, 0, 262144, 1048576, CancellationToken.None);

        await act.Should().ThrowAsync<DriveUploadSessionExpiredException>();
    }

    [Fact]
    public async Task A_throttled_chunk_surfaces_as_a_rate_limit_for_the_caller_to_resume_from()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.RateLimited(retryAfter: "12"));
        var client = DriveClientHarness.Create(stub);
        using var content = new MemoryStream(new byte[262144]);

        var act = async () => await client.WriteChunkAsync(
            SessionUri, content, 0, 262144, 1048576, CancellationToken.None);

        // Nothing retried it, because nothing could. The caller re-probes the session and sends the
        // chunk again from a stream that still has bytes in it.
        var thrown = await act.Should().ThrowAsync<DriveRateLimitedException>();
        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(12));
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_chunk_that_Drive_would_silently_ignore_never_leaves_the_process()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete("bytes=0-1"));
        var client = DriveClientHarness.Create(stub);
        using var content = new MemoryStream(new byte[1000]);

        // 1000 bytes is neither a multiple of 256 KiB nor the end of the file.
        var act = async () => await client.WriteChunkAsync(
            SessionUri, content, 0, 1000, 1048576, CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>();
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_probe_asks_without_sending_anything()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.ResumeIncomplete("bytes=0-1023"));
        var client = DriveClientHarness.Create(stub);

        var confirmed = await client.GetConfirmedLengthAsync(SessionUri, 4096, CancellationToken.None);

        confirmed.Should().Be(1024);

        var request = stub.LastRequest;
        request.Method.Should().Be(HttpMethod.Put);
        request.ContentHeader("Content-Range").Should().Be("bytes */4096");
        request.Body.Should().BeEmpty();

        // Empty body, so this one is safe to replay and is deliberately not opted out of the retry.
        request.MarkedNonRewindable.Should().BeFalse();
    }

    [Fact]
    public async Task A_probe_answered_with_200_means_the_upload_already_finished()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"id":"1AbC"}"""));

        var client = DriveClientHarness.Create(stub);

        var confirmed = await client.GetConfirmedLengthAsync(SessionUri, 4096, CancellationToken.None);

        confirmed.Should().Be(4096);
    }

    [Fact]
    public async Task A_probe_against_a_dead_session_is_not_a_generic_failure()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.NotFound, """{"error":{"code":404}}"""));

        var client = DriveClientHarness.Create(stub);

        var act = async () => await client.GetConfirmedLengthAsync(SessionUri, 4096, CancellationToken.None);

        await act.Should().ThrowAsync<DriveUploadSessionExpiredException>()
            .WithMessage("*started over*");
    }
}
