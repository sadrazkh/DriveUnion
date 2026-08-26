using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Uploads;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Uploads;

/// <summary>
/// «Go and get this for me»: the queue, and the pull that follows it.
///
/// <para><c>RemoteAddressPolicyTests</c> holds which addresses this server will dial, which is the
/// dangerous half. This holds the rest: that a URL that cannot work is refused before a job exists,
/// that a fetch which does work lands a real file in the right workspace, and that everything the
/// far end can do wrong is survived rather than believed.</para>
/// </summary>
public class RemoteFetchTests
{
    private const string Url = "https://files.example.test/report.pdf";

    private static byte[] Body(int length)
    {
        var content = new byte[length];
        for (var i = 0; i < length; i++) content[i] = (byte)((i * 29 + 5) % 251);

        return content;
    }

    [Theory]
    [InlineData("file:///etc/passwd", RemoteSourceRefusal.UnsupportedScheme)]
    [InlineData("gopher://example.test/1", RemoteSourceRefusal.UnsupportedScheme)]
    [InlineData("ftp://example.test/x", RemoteSourceRefusal.UnsupportedScheme)]
    [InlineData("https://user:secret@example.test/x", RemoteSourceRefusal.CarriesCredentials)]
    [InlineData("not a url", RemoteSourceRefusal.Malformed)]
    [InlineData("/relative/path", RemoteSourceRefusal.Malformed)]
    [InlineData("", RemoteSourceRefusal.Malformed)]
    public void A_link_that_cannot_work_is_refused_by_its_shape(string url, RemoteSourceRefusal expected)
    {
        // file: reads this server's own disk and gopher: has been used to speak other protocols
        // entirely through a URL fetcher. Two schemes are named and everything else is refused —
        // and a URL carrying credentials is refused rather than stripped, because they would be
        // logged, stored on the row, and sent to whatever the host turned out to be.
        RemoteSource.Inspect(url, out _).Should().Be(expected);
    }

    [Fact]
    public async Task A_refused_link_leaves_no_job_behind()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var result = await harness.Fetches().StartAsync(tenant.Id, null, "file:///etc/passwd", default);

        result.Started.Should().BeFalse();
        result.Refusal.Should().Be(RemoteSourceRefusal.UnsupportedScheme);

        (await harness.Db.RemoteFetches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_file_arrives_in_the_workspace_that_asked_for_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var content = Body(300_000);
        var source = new StubSource(content, "report.pdf", "application/pdf");

        (await harness.Fetches().StartAsync(tenant.Id, null, Url, default)).Started.Should().BeTrue();

        (await harness.Fetcher(source).RunOnceAsync(5, default)).Should().Be(1);

        var fetch = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();
        fetch.Status.Should().Be(RemoteFetchStatus.Completed);
        fetch.StoredFileId.Should().NotBeNull();
        fetch.SizeBytes.Should().Be(content.Length);

        var file = await harness.Db.StoredFiles.AsNoTracking().SingleAsync();
        file.TenantId.Should().Be(tenant.Id);
        file.Name.Should().Be("report.pdf");
        file.SizeBytes.Should().Be(content.Length);

        // The bytes, not just the row. A fetch that wrote a catalogue entry and no content would
        // pass every assertion above it.
        harness.Drive.Files[file.DriveFileId].Content.Should().Equal(content);
    }

    [Fact]
    public async Task A_source_that_will_not_say_how_big_it_is_is_refused_before_anything_is_reserved()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var source = new StubSource(Body(1024), "x.bin", "application/octet-stream")
        {
            StateLength = false,
        };

        await harness.Fetches().StartAsync(tenant.Id, null, Url, default);
        await harness.Fetcher(source).RunOnceAsync(5, default);

        // Storage needs a total before it will open a resumable session, so a source that will not
        // give one cannot be started — and the customer is told that rather than watching a job that
        // stalls. Nothing was reserved on the way.
        var fetch = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();
        fetch.FailureReason.Should().Contain("how big");

        (await harness.Db.UploadSessions.CountAsync()).Should().Be(0);
        (await harness.Db.StoredFiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_source_that_stops_halfway_does_not_leave_a_file_that_looks_finished()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        // Promises 300,000 bytes and sends 100,000. The one failure that must never look like
        // success: a file of the right name, the wrong length, and no error attached to it.
        var source = new StubSource(Body(100_000), "half.bin", "application/octet-stream")
        {
            ClaimedLength = 300_000,
        };

        await harness.Fetches().StartAsync(tenant.Id, null, Url, default);
        await harness.Fetcher(source).RunOnceAsync(5, default);

        (await harness.Db.StoredFiles.CountAsync()).Should().Be(0);

        var fetch = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();
        fetch.StoredFileId.Should().BeNull();
        fetch.FailureReason.Should().Contain("stopped after");
    }

    [Fact]
    public async Task A_source_that_answers_with_an_error_is_retried_and_then_given_up_on()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var source = new StubSource([], "x", "text/plain") { Status = HttpStatusCode.NotFound };

        await harness.Fetches().StartAsync(tenant.Id, null, Url, default);

        // Three passes, three attempts. A source that is down for a minute is worth another go; one
        // that is gone will answer the same on the fourth try as on the first.
        for (var i = 0; i < RemoteFetch.MaxAttempts; i++)
        {
            await harness.Fetcher(source).RunOnceAsync(1, default);
        }

        var fetch = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();
        fetch.Status.Should().Be(RemoteFetchStatus.Failed);
        fetch.Attempts.Should().Be(RemoteFetch.MaxAttempts);
        fetch.FailureReason.Should().Contain("404");
    }

    [Fact]
    public async Task One_workspace_cannot_queue_the_operators_whole_line()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        for (var i = 0; i < RemoteFetch.MostInFlightPerTenant; i++)
        {
            (await harness.Fetches().StartAsync(tenant.Id, null, $"{Url}?n={i}", default))
                .Started.Should().BeTrue();
        }

        // Without a cap this is a free bandwidth proxy: paste a thousand links and the operator pays
        // for a thousand transfers nobody chose to make.
        var refused = await harness.Fetches().StartAsync(tenant.Id, null, Url, default);

        refused.Started.Should().BeFalse();
        refused.Detail.Should().Be("queue_full");
    }

    [Fact]
    public async Task One_workspace_cannot_see_or_stop_anothers_fetch()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");

        var started = await harness.Fetches().StartAsync(theirs.Id, null, Url, default);

        (await harness.Fetches().ListAsync(mine.Id, default)).Should().BeEmpty();

        // Not found rather than found and refused, like every other tenant-scoped read here.
        (await harness.Fetches().CancelAsync(mine.Id, started.FetchId!.Value, default))
            .Should().BeFalse();

        (await harness.Fetches().CancelAsync(theirs.Id, started.FetchId!.Value, default))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("report.pdf", null, "report.pdf")]
    [InlineData(null, "https://x.test/a/b/quarterly%20report.pdf", "quarterly report.pdf")]
    [InlineData("../../etc/passwd", null, "etcpasswd")]
    [InlineData("a/b/c.bin", null, "abc.bin")]
    [InlineData("C:\\Windows\\x.dll", null, "CWindowsx.dll")]
    public void The_name_is_taken_from_the_far_end_and_stripped_of_anything_that_is_a_path(
        string? stated,
        string? url,
        string expected)
    {
        // Servers really do send «filename="../../etc/passwd"». Nothing downstream here joins it onto
        // a path, but the name is stored, shown, and put into a Content-Disposition of our own — so
        // it stops being a path at the point it arrives rather than at each of the three places it
        // is later used.
        var disposition = stated is null
            ? null
            : new ContentDispositionHeaderValue("attachment") { FileName = stated };

        RemoteFetcher.NameFor(url ?? "https://x.test/", disposition).Should().Be(expected);
    }

    [Fact]
    public async Task An_address_this_server_will_not_dial_is_said_plainly_and_not_retried()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        // HttpClient wraps whatever a connect callback throws, so the refusal arrives buried inside
        // an HttpRequestException. Read off the top of the chain it reads as «the source could not
        // be reached» — true of a host that is merely down, and the wrong sentence for one the
        // customer is not allowed to ask for. It took a real request to the metadata address to see
        // that, which is why this test exists in the shape it does.
        var wrapped = new HttpRequestException(
            "connection failed",
            new RemoteAddressRefusedException("169.254.169.254 is refused."));

        await harness.Fetches().StartAsync(tenant.Id, null, Url, default);
        await harness.Fetcher(new ThrowingSource(wrapped)).RunOnceAsync(5, default);

        var fetch = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();

        fetch.FailureReason.Should().Contain("not one this server will fetch from");

        // And once, not three times. A refused address will still be refused in a minute, so retries
        // are two more DNS lookups and two more minutes before the same sentence.
        fetch.Status.Should().Be(RemoteFetchStatus.Failed);
        fetch.Attempts.Should().Be(1);
    }

    [Fact]
    public void A_source_that_offers_no_name_at_all_still_gets_one()
    {
        // A file with no name is worse than a file with a dull one.
        RemoteFetcher.NameFor("https://x.test/", null).Should().StartWith("fetched-");
    }
}

/// <summary>
/// A far end that answers however a test needs it to.
///
/// <para>An <see cref="HttpMessageHandler"/> rather than a fake of our own interface, because what
/// is under test is how the fetcher reads an HTTP response — its length header, its disposition, a
/// body that stops early — and a fake that answered in our own vocabulary would be agreeing with the
/// fetcher's assumptions rather than checking them.</para>
/// </summary>
internal sealed class StubSource(byte[] content, string fileName, string mimeType) : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>What the header claims, when a test wants it to disagree with the body.</summary>
    public long? ClaimedLength { get; set; }

    /// <summary>False sends no Content-Length at all — a chunked response.</summary>
    public bool StateLength { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(Status)
        {
            Content = new StreamContent(new MemoryStream(content)),
        };

        if (Status == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = fileName };

            if (StateLength)
            {
                response.Content.Headers.ContentLength = ClaimedLength ?? content.LongLength;
            }
            else
            {
                response.Content.Headers.ContentLength = null;
            }
        }

        return Task.FromResult(response);
    }
}


/// <summary>A far end that cannot be reached, in whichever way the test needs.</summary>
internal sealed class ThrowingSource(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(failure);
}
