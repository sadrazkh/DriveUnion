using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.S3;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.S3;

/// <summary>
/// S3 multipart upload: parts staged on disk and assembled into one file.
///
/// <para>The case worth having a test for is the one that made staging necessary at all — parts
/// arriving out of order. The AWS CLI sends ten at once by default, so «part 7 before part 3» is the
/// normal case rather than an edge, and Drive's resumable session acknowledges only a contiguous
/// prefix. A gateway that streamed parts straight through would work perfectly against a client that
/// happened to be single-threaded and corrupt every object from one that was not.</para>
///
/// <para>These use a real directory under the test run's temporary path, because what is being
/// asserted is that bytes on disk come back in the right order — an in-memory fake for the staging
/// volume would be asserting that the fake works.</para>
/// </summary>
public sealed class S3MultipartTests : IDisposable
{
    private readonly string _staging =
        Path.Combine(Path.GetTempPath(), "driveunion-s3-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Parts_uploaded_out_of_order_assemble_in_the_order_the_completion_names()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var multipart = Store(harness);
        var begun = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "big.bin", "big.bin", null, "application/octet-stream", default);

        begun.Succeeded.Should().BeTrue();

        var one = Bytes('a', 2048);
        var two = Bytes('b', 2048);
        var three = Bytes('c', 1024);

        // Deliberately backwards. This is what a parallel client does, and it is the whole reason
        // parts are staged rather than streamed.
        var etagThree = await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 3, three);
        var etagOne = await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 1, one);
        var etagTwo = await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 2, two);

        var completed = await multipart.CompleteAsync(
            tenant.Id,
            begun.UploadId!.Value,
            [(1, etagOne), (2, etagTwo), (3, etagThree)],
            default);

        completed.Succeeded.Should().BeTrue();

        StoredBytes(harness, completed.StoredFileId!.Value).Should().Equal([.. one, .. two, .. three]);
    }

    [Fact]
    public async Task Re_uploading_a_part_replaces_it_rather_than_appending()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var multipart = Store(harness);
        var begun = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "retry.bin", "retry.bin", null, "application/octet-stream", default);

        await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 1, Bytes('x', 4096));

        // A client retrying a part it thinks failed. S3 allows this and clients do it; a gateway that
        // appended would assemble the object with that part twice in it.
        var replaced = Bytes('y', 1024);
        var etag = await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 1, replaced);

        var parts = await multipart.PartsAsync(tenant.Id, begun.UploadId!.Value, default);
        parts.Should().ContainSingle().Which.SizeBytes.Should().Be(1024);

        var completed = await multipart.CompleteAsync(tenant.Id, begun.UploadId!.Value, [(1, etag)], default);

        StoredBytes(harness, completed.StoredFileId!.Value).Should().Equal(replaced);
    }

    [Fact]
    public async Task A_completion_naming_a_part_that_was_never_uploaded_is_refused_whole()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var multipart = Store(harness);
        var begun = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "gap.bin", "gap.bin", null, "application/octet-stream", default);

        var etag = await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 1, Bytes('a', 1024));

        // Refused rather than assembled around: a file missing its middle is the wrong size and looks
        // perfectly fine, which is the worst kind of wrong for an object store.
        (await multipart.CompleteAsync(tenant.Id, begun.UploadId!.Value, [(1, etag), (2, etag)], default))
            .Outcome.Should().Be(S3MultipartOutcome.InvalidPart);

        // …and so is a part whose ETag disagrees with what was staged.
        (await multipart.CompleteAsync(tenant.Id, begun.UploadId!.Value, [(1, new string('0', 32))], default))
            .Outcome.Should().Be(S3MultipartOutcome.InvalidPart);

        (await multipart.CompleteAsync(tenant.Id, begun.UploadId!.Value, [], default))
            .Outcome.Should().Be(S3MultipartOutcome.EmptyCompletion);
    }

    [Fact]
    public async Task Aborting_takes_the_staged_bytes_with_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var staging = Staging();
        var multipart = Store(harness, staging);
        var begun = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "gone.bin", "gone.bin", null, "application/octet-stream", default);

        await StageAsync(multipart, tenant.Id, begun.UploadId!.Value, 1, Bytes('a', 4096));

        Directory.Exists(staging.DirectoryFor(begun.UploadId!.Value)).Should().BeTrue();

        (await multipart.AbortAsync(tenant.Id, begun.UploadId!.Value, default)).Succeeded.Should().BeTrue();

        // The row and the bytes, both. A row deleted without its bytes is a volume that fills with
        // parts nothing knows about, which no sweep would ever find.
        Directory.Exists(staging.DirectoryFor(begun.UploadId!.Value)).Should().BeFalse();
        (await multipart.PartsAsync(tenant.Id, begun.UploadId!.Value, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task One_workspace_cannot_reach_anothers_upload()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        harness.SeedAccount();

        var multipart = Store(harness);
        var begun = await multipart.BeginAsync(
            theirs.Id, Guid.NewGuid(), "theirs.bin", "theirs.bin", null, "application/octet-stream", default);

        var id = begun.UploadId!.Value;

        // The line this product is not allowed to cross, restated for an upload id that travels in
        // the clear on every part a client sends.
        (await multipart.StagePartAsync(mine.Id, id, 1, new MemoryStream(Bytes('a', 16)), default))
            .Outcome.Should().Be(S3MultipartOutcome.NotFound);

        (await multipart.PartsAsync(mine.Id, id, default)).Should().BeEmpty();

        (await multipart.CompleteAsync(mine.Id, id, [(1, "x")], default))
            .Outcome.Should().Be(S3MultipartOutcome.NotFound);

        (await multipart.AbortAsync(mine.Id, id, default))
            .Outcome.Should().Be(S3MultipartOutcome.NotFound);
    }

    [Fact]
    public async Task An_abandoned_upload_is_swept_and_a_fresh_one_is_not()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var staging = Staging();
        var clock = new Fakes.FixedClock(ServiceTestHarness.Now);
        var multipart = Store(harness, staging, clock);

        var abandoned = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "left.bin", "left.bin", null, "application/octet-stream", default);

        await StageAsync(multipart, tenant.Id, abandoned.UploadId!.Value, 1, Bytes('a', 1024));

        // A day and a minute later. The client that opened this one is gone, and nothing will ever
        // arrive to clean it up — which is precisely why a sweep exists rather than a finally block.
        clock.Advance(S3MultipartUpload.Abandoned + TimeSpan.FromMinutes(1));

        var fresh = await multipart.BeginAsync(
            tenant.Id, Guid.NewGuid(), "live.bin", "live.bin", null, "application/octet-stream", default);

        (await multipart.SweepAbandonedAsync(default)).Should().Be(1);

        Directory.Exists(staging.DirectoryFor(abandoned.UploadId!.Value)).Should().BeFalse();
        (await multipart.PartsAsync(tenant.Id, fresh.UploadId!.Value, default)).Should().BeEmpty();

        // The fresh one survives, which is the half a sweep gets wrong by being too eager.
        (await multipart.AbortAsync(tenant.Id, fresh.UploadId!.Value, default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_multipart_etag_is_built_the_way_s3_builds_one()
    {
        // «MD5 of the concatenated part MD5s, then a dash and the count». Clients that verify it
        // compute the same thing, so this is asserted against an independent computation rather than
        // against whatever the store happens to produce.
        var first = MD5.HashData(Bytes('a', 16));
        var second = MD5.HashData(Bytes('b', 16));
        var expected =
            Convert.ToHexStringLower(MD5.HashData([.. first, .. second])) + "-2";

        var actual = typeof(S3MultipartStore)
            .GetMethod("MultipartEtag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [new[] { Convert.ToHexStringLower(first), Convert.ToHexStringLower(second) }]);

        actual.Should().Be(expected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_staging)) Directory.Delete(_staging, recursive: true);
    }

    private S3StagingDirectory Staging() =>
        new(Options.Create(new S3StagingOptions { StagingDirectory = _staging, MinFreeBytes = 0 }));

    private S3MultipartStore Store(
        ServiceTestHarness harness,
        S3StagingDirectory? staging = null,
        TimeProvider? clock = null) =>
        new(
            harness.Db,
            staging ?? Staging(),
            harness.Uploads(),
            clock ?? harness.Clock,
            NullLogger<S3MultipartStore>.Instance);

    private static async Task<string> StageAsync(
        S3MultipartStore store,
        Guid tenantId,
        Guid uploadId,
        int partNumber,
        byte[] payload)
    {
        using var body = new MemoryStream(payload);
        var staged = await store.StagePartAsync(tenantId, uploadId, partNumber, body, default);

        staged.Succeeded.Should().BeTrue();

        return staged.ETag!;
    }

    /// <summary>What actually reached the fake Drive for a stored file — the assembled object.</summary>
    private static byte[] StoredBytes(ServiceTestHarness harness, Guid storedFileId)
    {
        var driveFileId = harness.Db.StoredFiles.Single(f => f.Id == storedFileId).DriveFileId;

        return harness.Drive.Files[driveFileId].Content;
    }

    private static byte[] Bytes(char fill, int length) => Encoding.ASCII.GetBytes(new string(fill, length));
}
