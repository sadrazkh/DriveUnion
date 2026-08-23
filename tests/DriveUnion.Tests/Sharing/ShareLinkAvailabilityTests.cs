using DriveUnion.Core.Sharing;
using FluentAssertions;

namespace DriveUnion.Tests.Sharing;

public class ShareLinkAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static ShareLink Link(
        bool active = true,
        DateTimeOffset? expiresAt = null,
        int? maxDownloads = null,
        int downloadCount = 0) => new()
        {
            Slug = "kx91mz",
            IsActive = active,
            ExpiresAt = expiresAt,
            MaxDownloads = maxDownloads,
            DownloadCount = downloadCount,
        };

    [Fact]
    public void A_fresh_unlimited_link_is_available()
    {
        Link().Evaluate(Now).Should().Be(ShareLinkAvailability.Available);
    }

    [Fact]
    public void A_revoked_link_is_revoked_even_when_nothing_else_is_wrong()
    {
        Link(active: false).Evaluate(Now).Should().Be(ShareLinkAvailability.Revoked);
    }

    [Fact]
    public void Expiry_is_inclusive_of_the_moment_it_arrives()
    {
        Link(expiresAt: Now).Evaluate(Now).Should().Be(ShareLinkAvailability.Expired);
        Link(expiresAt: Now.AddSeconds(1)).Evaluate(Now).Should().Be(ShareLinkAvailability.Available);
    }

    [Fact]
    public void The_cap_is_reached_at_the_cap_not_past_it()
    {
        // 500/500 is spent. Off by one here hands out a 501st download.
        Link(maxDownloads: 500, downloadCount: 499).Evaluate(Now).Should().Be(ShareLinkAvailability.Available);
        Link(maxDownloads: 500, downloadCount: 500).Evaluate(Now).Should().Be(ShareLinkAvailability.DownloadCapReached);
        Link(maxDownloads: 500, downloadCount: 501).Evaluate(Now).Should().Be(ShareLinkAvailability.DownloadCapReached);
    }

    [Fact]
    public void No_cap_means_no_cap()
    {
        Link(maxDownloads: null, downloadCount: 100_000).Evaluate(Now).Should().Be(ShareLinkAvailability.Available);
    }

    [Fact]
    public void Revocation_outranks_expiry_which_outranks_the_cap()
    {
        // The order only matters to the owner's panel and the logs — the visitor sees one card
        // either way — but "why is this link dead" should get the most actionable answer.
        Link(active: false, expiresAt: Now.AddDays(-1), maxDownloads: 1, downloadCount: 9)
            .Evaluate(Now).Should().Be(ShareLinkAvailability.Revoked);

        Link(expiresAt: Now.AddDays(-1), maxDownloads: 1, downloadCount: 9)
            .Evaluate(Now).Should().Be(ShareLinkAvailability.Expired);
    }
}
