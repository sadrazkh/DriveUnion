using DriveUnion.Core.Sharing;
using FluentAssertions;

namespace DriveUnion.Tests.Sharing;

/// <summary>
/// The rule that keeps a video viewer from spending a customer's download cap.
/// </summary>
public class DownloadCountingTests
{
    [Fact]
    public void A_request_with_no_range_is_a_download()
    {
        DownloadCounting.CountsAsDownload(null).Should().BeTrue();
        DownloadCounting.CountsAsDownload("").Should().BeTrue();
        DownloadCounting.CountsAsDownload("   ").Should().BeTrue();
    }

    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-1023")]
    [InlineData("BYTES=0-1023")]
    [InlineData("  bytes=0-1023  ")]
    public void A_request_starting_at_byte_zero_is_a_download(string range)
    {
        DownloadCounting.CountsAsDownload(range).Should().BeTrue();
    }

    [Theory]
    [InlineData("bytes=1024-")]
    [InlineData("bytes=1048576-2097151")]
    public void A_continuation_is_not_a_new_download(string range)
    {
        DownloadCounting.CountsAsDownload(range).Should().BeFalse();
    }

    [Fact]
    public void A_suffix_range_is_not_a_download()
    {
        // "the last 500 bytes" — a player reading a container's trailing index, not a download.
        DownloadCounting.CountsAsDownload("bytes=-500").Should().BeFalse();
    }

    [Fact]
    public void A_multipart_range_is_judged_by_its_first_spec()
    {
        DownloadCounting.CountsAsDownload("bytes=0-99,200-299").Should().BeTrue();
        DownloadCounting.CountsAsDownload("bytes=200-299,0-99").Should().BeFalse();
    }

    [Fact]
    public void A_malformed_range_is_not_billed()
    {
        DownloadCounting.CountsAsDownload("bytes=nonsense").Should().BeFalse();
        DownloadCounting.CountsAsDownload("bytes=").Should().BeFalse();
    }

    [Fact]
    public void An_unmodelled_unit_counts_once()
    {
        // Drive decides whether to honour it. Not understanding a header must not make downloads free.
        DownloadCounting.CountsAsDownload("items=0-10").Should().BeTrue();
    }

    [Fact]
    public void The_one_byte_probe_is_not_a_download()
    {
        // A <video> element opens with `bytes=0-0` to learn the length, then immediately asks for
        // the file. Counting the probe bills every playback twice.
        DownloadCounting.CountsAsDownload("bytes=0-0").Should().BeFalse();
        DownloadCounting.CountsAsDownload("bytes=0-1").Should().BeTrue();
    }

    [Fact]
    public void Scrubbing_through_a_video_costs_one_download_not_twenty()
    {
        string[] seeks =
        [
            "bytes=0-0",           // the player probes for length first
            "bytes=0-",            // press play
            "bytes=52428800-",     // seek
            "bytes=104857600-",    // seek
            "bytes=157286400-",    // seek
            "bytes=-1024",         // read the trailing index
        ];

        seeks.Count(DownloadCounting.CountsAsDownload).Should().Be(1);
    }
}
