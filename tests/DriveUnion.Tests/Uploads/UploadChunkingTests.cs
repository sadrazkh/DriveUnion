using DriveUnion.Core.Uploads;
using FluentAssertions;

namespace DriveUnion.Tests.Uploads;

/// <summary>
/// Drive does not reject a badly sized chunk loudly — it just stops acknowledging bytes, which on
/// the wire is indistinguishable from a stalled upload. These are the assertions that keep that
/// from ever reaching a customer.
/// </summary>
public class UploadChunkingTests
{
    [Fact]
    public void The_default_chunk_size_is_a_multiple_of_the_required_unit()
    {
        (UploadChunking.DefaultChunkSize % UploadChunking.DriveChunkMultiple).Should().Be(0);
        UploadChunking.IsValidChunkSize(UploadChunking.DefaultChunkSize).Should().BeTrue();
    }

    [Theory]
    [InlineData(256 * 1024)]
    [InlineData(8 * 1024 * 1024)]
    [InlineData(256 * 1024 * 1024)]
    public void Multiples_of_256_KiB_within_bounds_are_valid(int size)
    {
        UploadChunking.IsValidChunkSize(size).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(512 * 1024 * 1024)]
    public void Anything_else_is_not(int size)
    {
        UploadChunking.IsValidChunkSize(size).Should().BeFalse();
    }

    [Fact]
    public void A_middle_chunk_must_be_a_clean_multiple()
    {
        const long total = 100L * 1024 * 1024;

        UploadChunking.IsValidChunk(0, 32 * 1024 * 1024, total).Should().BeTrue();
        UploadChunking.IsValidChunk(32 * 1024 * 1024, 32 * 1024 * 1024, total).Should().BeTrue();
        UploadChunking.IsValidChunk(0, 32 * 1024 * 1024 + 1, total).Should().BeFalse();
    }

    [Fact]
    public void The_final_chunk_is_exempt_because_files_are_not_multiples_of_256_KiB()
    {
        const long total = 1000;
        UploadChunking.IsValidChunk(0, 1000, total).Should().BeTrue();

        const long bigger = 64L * 1024 * 1024 + 777;
        UploadChunking.IsValidChunk(64 * 1024 * 1024, 777, bigger).Should().BeTrue();
    }

    [Fact]
    public void A_chunk_may_not_run_past_the_end_of_the_file()
    {
        UploadChunking.IsValidChunk(offset: 900, length: 200, totalSize: 1000).Should().BeFalse();
    }

    [Fact]
    public void Empty_and_negative_chunks_are_rejected()
    {
        UploadChunking.IsValidChunk(0, 0, 1000).Should().BeFalse();
        UploadChunking.IsValidChunk(-1, 100, 1000).Should().BeFalse();
    }

    [Fact]
    public void Content_range_is_inclusive_on_both_ends()
    {
        UploadChunking.ContentRange(0, 1024, 4096).Should().Be("bytes 0-1023/4096");
        UploadChunking.ContentRange(1024, 1024, 4096).Should().Be("bytes 1024-2047/4096");
    }

    [Fact]
    public void The_probe_range_asks_without_sending()
    {
        UploadChunking.ProbeContentRange(4096).Should().Be("bytes */4096");
    }
}
