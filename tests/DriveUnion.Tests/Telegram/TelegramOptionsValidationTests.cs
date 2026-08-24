using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// What the deployment is refused at startup, rather than allowed to discover months later.
/// </summary>
public class TelegramOptionsValidationTests
{
    [Fact]
    public void A_delivery_lifetime_beyond_the_api_limit_is_refused_rather_than_truncated()
    {
        var options = Valid();
        options.DeliveryMessageTtlMinutes = 60 * 72;

        var result = Validate(options);

        // A bot may only delete a message sent less than 48 hours ago, so a longer lifetime is a
        // timer that arms, comes due and does nothing — with a line on the customer's screen
        // promising it would go. Clamping silently is how a number nobody meant becomes the number
        // in production.
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("48 hours");
    }

    [Fact]
    public void The_longest_honourable_lifetime_is_accepted()
    {
        var options = Valid();
        options.DeliveryMessageTtlMinutes = TelegramOptions.MaxDeliveryMessageTtlMinutes;

        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Off_is_the_default_and_is_valid()
    {
        var options = Valid();

        options.DeliveryMessageTtlMinutes.Should().Be(0);
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_ceiling_the_disk_cannot_hold_is_refused_at_startup()
    {
        var options = Valid();
        options.WorkDirectory = Path.GetTempPath();
        options.MaxSendBytes = 2_000_000_000;
        options.MaxReceiveBytes = 2_000_000_000;
        options.MaxConcurrentTransfers = 2;
        options.WorkDirHeadroomBytes = 1_000_000_000;

        // Two concurrent transfers of two gigabytes each way plus headroom is nine gigabytes, on a
        // volume with one.
        var result = Validate(options, freeBytes: 1_000_000_000);

        result.Failed.Should().BeTrue();

        // And the fix named in the message is a smaller ceiling rather than a smaller sweeper: over
        // the ceiling the bot hands over a share link, which is a working product, where a ceiling
        // the disk cannot hold is an outage.
        result.FailureMessage.Should().Contain("Telegram:MaxSendBytes");
    }

    [Fact]
    public void The_same_ceiling_on_a_volume_that_can_hold_it_is_accepted()
    {
        var options = Valid();
        options.WorkDirectory = Path.GetTempPath();
        options.MaxSendBytes = 2_000_000_000;
        options.MaxReceiveBytes = 2_000_000_000;
        options.MaxConcurrentTransfers = 2;
        options.WorkDirHeadroomBytes = 1_000_000_000;

        Validate(options, freeBytes: 50_000_000_000).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_machine_with_no_working_directory_is_not_asked_about_its_disk()
    {
        var options = Valid();
        options.WorkDirectory = null;
        options.MaxSendBytes = 2_000_000_000;

        // Development has no local Bot API server and therefore no working directory, so the
        // arithmetic has nothing to be true about.
        Validate(options, freeBytes: 1).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void An_endpoint_that_is_not_a_url_is_refused()
    {
        var options = Valid();
        options.ApiBaseUrl = "127.0.0.1:8081";

        Validate(options).Failed.Should().BeTrue();
    }

    private static TelegramOptions Valid() => new();

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(
        TelegramOptions options,
        long? freeBytes = null)
    {
        var disk = new TelegramTestHarness.FakeDiskSpace { FreeBytes = freeBytes };

        return new TelegramOptionsValidator(disk).Validate(null, options);
    }
}
