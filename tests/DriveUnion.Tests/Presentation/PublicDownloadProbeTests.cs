using System.Net;
using System.Reflection;
using System.Text;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Web.Controllers;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// <c>HEAD /d/{slug}/file</c>.
///
/// Players probe with HEAD before they stream, and the answer has to be the GET's headers with no
/// body — and it must not cost the customer a download, because a probe is not a download. Both
/// halves are asserted here rather than left to the route: the counting rule is what a customer is
/// billed on.
/// </summary>
public class PublicDownloadProbeTests
{
    private const string Slug = "kx91mzq4";

    private static readonly byte[] FileBytes = Encoding.UTF8.GetBytes("%PDF-1.7 one small file\n");

    [Fact]
    public async Task A_probe_answers_with_the_headers_the_stream_would_send()
    {
        var (probe, probed) = await ProbeAsync();
        var (stream, streamed) = await DownloadAsync(range: null);

        probed.Response.StatusCode.Should().Be(streamed.Response.StatusCode).And.Be(StatusCodes.Status200OK);
        probed.Response.ContentType.Should().Be(streamed.Response.ContentType).And.Be("application/pdf");
        probed.Response.ContentLength.Should().Be(streamed.Response.ContentLength).And.Be(FileBytes.Length);
        probed.Response.Headers.AcceptRanges.ToString()
            .Should().Be(streamed.Response.Headers.AcceptRanges.ToString()).And.Be("bytes");
        probed.Response.Headers.ContentDisposition.ToString()
            .Should().Be(streamed.Response.Headers.ContentDisposition.ToString())
            .And.Contain("Q3-Report-Final.pdf");

        probe.Should().BeOfType<EmptyResult>();
        stream.Should().BeOfType<EmptyResult>();
    }

    [Fact]
    public async Task A_probe_writes_no_body()
    {
        var (_, context) = await ProbeAsync();

        ((MemoryStream)context.Response.Body).Length.Should().Be(0);
    }

    [Fact]
    public async Task A_probe_is_not_a_download()
    {
        var reader = new RecordingLinkReader(Ticket());

        await ProbeAsync(reader);

        reader.Recorded.Should().Be(0);
    }

    [Fact]
    public async Task A_probe_never_opens_a_drive_stream()
    {
        // The size, name and type are all on the ticket. Reaching Google for them would put a
        // Google connection behind every probe of the one anonymous route in the product.
        var drive = new StubDriveClient();

        await ProbeAsync(drive: drive);

        drive.Opens.Should().Be(0);
    }

    [Fact]
    public async Task A_range_on_a_probe_is_ignored_rather_than_answered_with_a_206()
    {
        var (_, context) = await ProbeAsync(range: "bytes=1024-2047");

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.ContentLength.Should().Be(FileBytes.Length);
        context.Response.Headers.ContentRange.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task A_whole_file_get_still_counts_once()
    {
        // The guard against fixing HEAD by making every request free.
        var reader = new RecordingLinkReader(Ticket());

        await DownloadAsync(range: null, reader);

        reader.Recorded.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_slug_is_the_same_refusal_for_a_probe_as_for_a_stream()
    {
        var reader = new RecordingLinkReader(ticket: null);

        var (probe, probed) = await ProbeAsync(reader);
        var (stream, streamed) = await DownloadAsync(range: null, new RecordingLinkReader(ticket: null));

        // Revoked, expired, capped and never-existed are one card; the verb must not be a fourth
        // way to tell them apart. The status rides on the result rather than on the response
        // because a unit test stops before the result executes.
        var probeView = probe.Should().BeOfType<ViewResult>().Subject;
        var streamView = stream.Should().BeOfType<ViewResult>().Subject;

        probeView.StatusCode.Should().Be(streamView.StatusCode).And.Be(StatusCodes.Status404NotFound);
        probeView.ViewName.Should().Be(streamView.ViewName);
        probed.Response.Headers.CacheControl.ToString()
            .Should().Be(streamed.Response.Headers.CacheControl.ToString()).And.Be("no-store");

        reader.Recorded.Should().Be(0);
    }

    [Fact]
    public async Task A_slug_that_cannot_exist_never_reaches_the_database()
    {
        var reader = new RecordingLinkReader(Ticket());

        // Six characters is the comp's slug length, not this product's eight.
        var (result, _) = await ProbeAsync(reader, slug: "kx91mz");

        reader.Resolutions.Should().Be(0);
        result.Should().BeOfType<ViewResult>().Subject.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void The_stream_route_is_mapped_for_head_as_well_as_get()
    {
        var stream = Method(nameof(PublicDownloadController.Download));
        var probe = Method(nameof(PublicDownloadController.Probe));

        stream.GetCustomAttributes<HttpGetAttribute>().Single().Template.Should().Be("/d/{slug}/file");
        probe.GetCustomAttributes<HttpHeadAttribute>().Single().Template.Should().Be("/d/{slug}/file");

        // Nothing else may be routed onto the probe: the moment a GET lands on it, a download is
        // free, because this action has no recorder in it.
        probe.GetCustomAttributes<HttpMethodAttribute>()
            .Should().ContainSingle().Which.Should().BeOfType<HttpHeadAttribute>();

        // The stream and the probe share the rate limiter; a probe is cheap but not free.
        probe.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
            .Should().Be(DriveUnionRateLimits.PublicDownload);
    }

    private static MethodInfo Method(string name) =>
        typeof(PublicDownloadController).GetMethod(name)
        ?? throw new InvalidOperationException($"PublicDownloadController.{name} is gone.");

    private static PublicDownloadTicket Ticket() => new(
        ShareLinkId: Guid.Parse("3f4a0f2a-7f47-4d05-9a1c-2a2f31d3f0b1"),
        GoogleAccountId: Guid.Parse("6c1d1a44-9f0e-4b6a-8f2b-05e0b5a8c3d2"),
        DriveFileId: "1aB9Zk",
        FileName: "Q3-Report-Final.pdf",
        MimeType: "application/pdf",
        SizeBytes: FileBytes.Length);

    private static async Task<(IActionResult Result, DefaultHttpContext Context)> ProbeAsync(
        IPublicLinkReader? reader = null,
        IDriveClient? drive = null,
        string? range = null,
        string slug = Slug)
    {
        var (controller, context) = Build(reader ?? new RecordingLinkReader(Ticket()), drive, range);
        var result = await controller.Probe(slug, null, CancellationToken.None);

        return (result, context);
    }

    private static async Task<(IActionResult Result, DefaultHttpContext Context)> DownloadAsync(
        string? range,
        IPublicLinkReader? reader = null)
    {
        var (controller, context) = Build(reader ?? new RecordingLinkReader(Ticket()), null, range);
        var result = await controller.Download(Slug, null, CancellationToken.None);

        return (result, context);
    }

    private static (PublicDownloadController Controller, DefaultHttpContext Context) Build(
        IPublicLinkReader reader,
        IDriveClient? drive,
        string? range)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (range is not null) context.Request.Headers.Range = range;

        var controller = new PublicDownloadController(
            reader,
            drive ?? new StubDriveClient(),
            new FixedIpHasher(),
            Options.Create(new DriveUnionWebOptions()),
            NullLogger<PublicDownloadController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        return (controller, context);
    }

    private sealed class RecordingLinkReader(PublicDownloadTicket? ticket) : IPublicLinkReader
    {
        public int Recorded { get; private set; }

        public int Resolutions { get; private set; }

        /// <summary>Slots taken and slots handed back. This double has no cap, so it always grants.</summary>
        public int Reserved { get; private set; }

        public int Released { get; private set; }

        public Task<PublicLinkResolution> ResolveAsync(string slug, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Neither of the byte routes renders the landing page.");

        public Task<PublicDownloadTicket?> ResolveForDownloadAsync(string slug, CancellationToken cancellationToken)
        {
            Resolutions++;
            return Task.FromResult(ticket);
        }

        public Task<bool> TryReserveDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken)
        {
            Reserved++;
            return Task.FromResult(true);
        }

        public Task ReleaseDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken)
        {
            Released++;
            return Task.CompletedTask;
        }

        public Task RecordDownloadAsync(
            Guid shareLinkId,
            string ipHash,
            string? userAgent,
            CancellationToken cancellationToken)
        {
            Recorded++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDriveClient : IDriveClient
    {
        public int Opens { get; private set; }

        public Task<DriveDownload> OpenDownloadAsync(
            Guid accountId,
            string driveFileId,
            string? rangeHeader,
            CancellationToken cancellationToken)
        {
            Opens++;

            return Task.FromResult(new DriveDownload(
                new MemoryStream(FileBytes),
                "application/pdf",
                FileBytes.Length,
                contentRange: null,
                isPartial: false,
                new NoopOwner()));
        }

        public Task<DriveResumableSession> BeginResumableUploadAsync(
            Guid accountId,
            DriveUploadRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DriveChunkOutcome> WriteChunkAsync(
            Uri sessionUri,
            Stream content,
            long offset,
            long length,
            long totalSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> GetConfirmedLengthAsync(
            Uri sessionUri,
            long totalSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> EnsureFolderAsync(
            Guid accountId,
            string folderName,
            string? parentFolderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(
            Guid accountId,
            string driveFileId,
            string? fromFolderId,
            string toFolderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(
            Guid accountId,
            string driveFileId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DriveStorageQuota> GetStorageQuotaAsync(
            Guid accountId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private sealed class NoopOwner : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FixedIpHasher : IDownloadIpHasher
    {
        public string Hash(IPAddress? address) => "hash";
    }
}
