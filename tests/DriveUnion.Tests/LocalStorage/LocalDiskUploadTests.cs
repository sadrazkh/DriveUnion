using DriveUnion.Core.Abstractions;
using FluentAssertions;

namespace DriveUnion.Tests.LocalStorage;

/// <summary>
/// The upload leg of the local-disk backend.
///
/// The rule every test here circles is the one the real Drive imposes: a resumable session
/// acknowledges <b>one contiguous prefix anchored at zero</b>, and the only authority on how long
/// that prefix is is the server. What the client believes it sent is not evidence — that belief is
/// exactly what a dropped connection makes wrong.
/// </summary>
public class LocalDiskUploadTests
{
    private const int Chunk = LocalDiskHarness.Chunk;

    /// <summary>Two whole chunks and a short last one, which is the only shape Drive allows.</summary>
    private const int ThreeChunks = (2 * Chunk) + 1000;

    [Fact]
    public async Task A_chunked_upload_assembles_the_file_byte_for_byte()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var metadata = await LocalDiskHarness.UploadAsync(client, content);

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, metadata.FileId, null, CancellationToken.None);

        var served = await LocalDiskHarness.ReadAllAsync(download);

        served.Should().Equal(content);
    }

    [Fact]
    public async Task The_last_chunk_is_the_one_that_carries_the_files_metadata()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Chunk + 7);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("گزارش.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var first = new MemoryStream(content, 0, Chunk, writable: false);
        var opening = await client.WriteChunkAsync(
            session.SessionUri, first, 0, Chunk, content.LongLength, CancellationToken.None);

        opening.ConfirmedLength.Should().Be(Chunk);
        opening.Completed.Should().BeNull("only the chunk that finishes the file carries metadata");

        using var last = new MemoryStream(content, Chunk, 7, writable: false);
        var closing = await client.WriteChunkAsync(
            session.SessionUri, last, Chunk, 7, content.LongLength, CancellationToken.None);

        closing.ConfirmedLength.Should().Be(content.LongLength);
        closing.Completed.Should().NotBeNull();
        closing.Completed!.FileId.Should().StartWith("ld-");
        closing.Completed.Name.Should().Be("گزارش.pdf");
        closing.Completed.MimeType.Should().Be("application/pdf");
        closing.Completed.SizeBytes.Should().Be(content.LongLength);
    }

    [Fact]
    public async Task An_interrupted_chunk_confirms_nothing_and_resumes_from_the_prefix_that_landed()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("film.mp4", "video/mp4", content.LongLength, null),
            CancellationToken.None);

        using var first = new MemoryStream(content, 0, Chunk, writable: false);
        await client.WriteChunkAsync(
            session.SessionUri, first, 0, Chunk, content.LongLength, CancellationToken.None);

        // The second chunk dies a third of the way through, the way a chunk dies when the connection
        // carrying it does.
        using var dying = new DyingStream(content.AsSpan(Chunk, Chunk).ToArray(), Chunk / 3);
        var interrupted = async () => await client.WriteChunkAsync(
            session.SessionUri, dying, Chunk, Chunk, content.LongLength, CancellationToken.None);

        await interrupted.Should().ThrowAsync<IOException>();

        // The bytes that did arrive are not acknowledged: they are the tail of a chunk nobody can
        // prove the rest of, and counting them would resume the upload inside a chunk boundary.
        var confirmed = await client.GetConfirmedLengthAsync(
            session.SessionUri, content.LongLength, CancellationToken.None);

        confirmed.Should().Be(Chunk);

        // A client that trusted its own record would continue here, having "sent" two chunks.
        using var believed = new MemoryStream(content, 2 * Chunk, 1000, writable: false);
        var fromWhatTheClientThinks = async () => await client.WriteChunkAsync(
            session.SessionUri, believed, 2 * Chunk, 1000, content.LongLength, CancellationToken.None);

        await fromWhatTheClientThinks.Should().ThrowAsync<DriveApiException>()
            .WithMessage("*does not continue this session*");

        using var second = new MemoryStream(content, Chunk, Chunk, writable: false);
        var resumed = await client.WriteChunkAsync(
            session.SessionUri, second, Chunk, Chunk, content.LongLength, CancellationToken.None);

        resumed.ConfirmedLength.Should().Be(2 * Chunk);

        using var third = new MemoryStream(content, 2 * Chunk, 1000, writable: false);
        var finished = await client.WriteChunkAsync(
            session.SessionUri, third, 2 * Chunk, 1000, content.LongLength, CancellationToken.None);

        finished.Completed.Should().NotBeNull();

        await using var download = await client.OpenDownloadAsync(
            LocalDiskHarness.AccountId, finished.Completed!.FileId, null, CancellationToken.None);

        // The whole point: the half chunk that arrived first was overwritten, not kept.
        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task A_chunk_at_the_wrong_offset_is_refused()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var ahead = new MemoryStream(content, Chunk, Chunk, writable: false);
        var jumped = async () => await client.WriteChunkAsync(
            session.SessionUri, ahead, Chunk, Chunk, content.LongLength, CancellationToken.None);

        await jumped.Should().ThrowAsync<DriveApiException>()
            .WithMessage("*does not continue this session*0 bytes confirmed*");

        var confirmed = await client.GetConfirmedLengthAsync(
            session.SessionUri, content.LongLength, CancellationToken.None);

        confirmed.Should().Be(0);
    }

    [Fact]
    public async Task A_chunk_body_shorter_than_it_declared_is_refused()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var truncated = new MemoryStream(content, 0, 4096, writable: false);
        var undersized = async () => await client.WriteChunkAsync(
            session.SessionUri, truncated, 0, Chunk, content.LongLength, CancellationToken.None);

        await undersized.Should().ThrowAsync<DriveApiException>()
            .WithMessage("*carried 4096 bytes but declared 262144*");

        (await client.GetConfirmedLengthAsync(session.SessionUri, content.LongLength, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_chunk_body_longer_than_it_declared_is_refused()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var oversized = new MemoryStream(content, 0, Chunk + 1, writable: false);
        var overrun = async () => await client.WriteChunkAsync(
            session.SessionUri, oversized, 0, Chunk, content.LongLength, CancellationToken.None);

        await overrun.Should().ThrowAsync<DriveApiException>().WithMessage("*more than the*declared*");

        (await client.GetConfirmedLengthAsync(session.SessionUri, content.LongLength, CancellationToken.None))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_chunk_that_is_not_a_multiple_of_256_KiB_is_refused_before_it_is_written()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var ragged = new MemoryStream(content, 0, 100_000, writable: false);
        var refused = async () => await client.WriteChunkAsync(
            session.SessionUri, ragged, 0, 100_000, content.LongLength, CancellationToken.None);

        // Drive answers this by silently not acknowledging the bytes, which reads like a stalled
        // network. Both clients say it out loud instead.
        await refused.Should().ThrowAsync<DriveApiException>().WithMessage("*Refusing to write*");
    }

    [Fact]
    public async Task An_open_session_outlives_the_process_that_opened_it()
    {
        using var harness = new LocalDiskHarness();
        var content = LocalDiskHarness.Content(ThreeChunks);

        var before = harness.Create();
        var session = await before.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("film.mp4", "video/mp4", content.LongLength, null),
            CancellationToken.None);

        using var first = new MemoryStream(content, 0, Chunk, writable: false);
        await before.WriteChunkAsync(
            session.SessionUri, first, 0, Chunk, content.LongLength, CancellationToken.None);

        // A second client shares nothing with the first but the directory — which is the whole
        // difference between this backend and the in-memory double the rest of the suite uses.
        var after = harness.Create();

        (await after.GetConfirmedLengthAsync(session.SessionUri, content.LongLength, CancellationToken.None))
            .Should().Be(Chunk);

        using var second = new MemoryStream(content, Chunk, Chunk, writable: false);
        await after.WriteChunkAsync(
            session.SessionUri, second, Chunk, Chunk, content.LongLength, CancellationToken.None);

        using var third = new MemoryStream(content, 2 * Chunk, 1000, writable: false);
        var finished = await after.WriteChunkAsync(
            session.SessionUri, third, 2 * Chunk, 1000, content.LongLength, CancellationToken.None);

        finished.Completed.Should().NotBeNull();

        await using var download = await after.OpenDownloadAsync(
            LocalDiskHarness.AccountId, finished.Completed!.FileId, null, CancellationToken.None);

        (await LocalDiskHarness.ReadAllAsync(download)).Should().Equal(content);
    }

    [Fact]
    public async Task An_expired_session_cannot_be_written_to()
    {
        using var harness = new LocalDiskHarness(TimeSpan.FromHours(1));
        var client = harness.Create();
        var content = LocalDiskHarness.Content(Chunk);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromHours(2));

        using var chunk = new MemoryStream(content, 0, Chunk, writable: false);
        var late = async () => await client.WriteChunkAsync(
            session.SessionUri, chunk, 0, Chunk, content.LongLength, CancellationToken.None);

        await late.Should().ThrowAsync<DriveUploadSessionExpiredException>().WithMessage("*expired*");
    }

    [Fact]
    public async Task An_expired_session_cannot_be_probed_either()
    {
        using var harness = new LocalDiskHarness(TimeSpan.FromHours(1));
        var client = harness.Create();

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", 4096, null),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromHours(1));

        // Expiry is inclusive: the session is dead at the instant it expires, not a tick later.
        var probe = async () => await client.GetConfirmedLengthAsync(
            session.SessionUri, 4096, CancellationToken.None);

        await probe.Should().ThrowAsync<DriveUploadSessionExpiredException>();
    }

    [Fact]
    public async Task A_session_URI_this_backend_never_issued_is_gone_rather_than_broken()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();

        var invented = new Uri("https://upload.fake-drive.invalid/session/1");
        var probe = async () => await client.GetConfirmedLengthAsync(invented, 4096, CancellationToken.None);

        // The same answer Drive gives for a session URI it does not recognise — and the reason no
        // part of a caller's URI is ever turned into a path.
        await probe.Should().ThrowAsync<DriveUploadSessionExpiredException>();
    }

    [Fact]
    public async Task A_replayed_final_chunk_answers_with_the_same_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(4096);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", content.LongLength, null),
            CancellationToken.None);

        using var only = new MemoryStream(content, writable: false);
        var first = await client.WriteChunkAsync(
            session.SessionUri, only, 0, content.LongLength, content.LongLength, CancellationToken.None);

        using var again = new MemoryStream(content, writable: false);
        var replay = await client.WriteChunkAsync(
            session.SessionUri, again, 0, content.LongLength, content.LongLength, CancellationToken.None);

        // Drive answers a repeated last chunk with the finished file rather than an error, so a
        // retried request cannot turn into a second file.
        replay.Completed!.FileId.Should().Be(first.Completed!.FileId);
        replay.ConfirmedLength.Should().Be(content.LongLength);
    }

    [Fact]
    public async Task A_chunk_that_claims_a_different_total_belongs_to_a_different_file()
    {
        using var harness = new LocalDiskHarness();
        var client = harness.Create();
        var content = LocalDiskHarness.Content(8192);

        var session = await client.BeginResumableUploadAsync(
            LocalDiskHarness.AccountId,
            new DriveUploadRequest("report.pdf", "application/pdf", 4096, null),
            CancellationToken.None);

        // Well-formed on its own — one final chunk of 8192 bytes — but this session was opened for a
        // 4096-byte file, and Drive rejects a Content-Range whose total contradicts the one the
        // session was initiated with.
        using var chunk = new MemoryStream(content, writable: false);
        var mismatched = async () => await client.WriteChunkAsync(
            session.SessionUri, chunk, 0, 8192, 8192, CancellationToken.None);

        await mismatched.Should().ThrowAsync<DriveApiException>().WithMessage("*some other file*");
    }
}
