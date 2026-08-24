using DriveUnion.Core.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

public class TelegramFormatsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(18_400_000, "18.4 MB")]
    [InlineData(50_000_000, "50.0 MB")]
    [InlineData(2_000_000_000, "2.0 GB")]
    public void Sizes_are_decimal_and_latin(long bytes, string expected)
    {
        // Decimal, because the ceiling they are compared against is decimal. A card that said
        // «1.9 GB» for a file the bot then refused would be telling the truth in the wrong base.
        //
        // Latin digits, because a byte size is an LTR technical readout — the same rule the panel
        // holds to, where «۳ روز پیش» sits beside 18.4 MB on one line.
        TelegramFormats.Bytes(bytes).Should().Be(expected);
    }

    [Fact]
    public void The_new_ceiling_renders_on_the_cards_second_line()
    {
        // The size the card has to draw at the deployed ceiling, and the reason this is a fixture:
        // the branch is only ever exercised on a file nobody wants to make in a test.
        TelegramFormats.Bytes(1_999_999_999).Should().Be("2.0 GB");
    }

    [Fact]
    public void Elapsed_time_is_persian_prose_with_persian_digits()
    {
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        TelegramFormats.Ago(now.AddDays(-3), now).Should().Be("۳ روز پیش");
        TelegramFormats.Ago(now.AddHours(-5), now).Should().Be("۵ ساعت پیش");
        TelegramFormats.Ago(now.AddSeconds(-10), now).Should().Be("همین حالا");
    }

    [Fact]
    public void A_clock_that_reads_backwards_does_not_produce_a_negative_age()
    {
        var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        TelegramFormats.Ago(now.AddMinutes(5), now).Should().Be("همین حالا");
    }

    [Fact]
    public void Counts_in_prose_are_persian()
    {
        // «لینک‌ها (۲)» — a count a person reads, so Persian; and the grouped form for larger ones.
        TelegramFormats.Digits(2).Should().Be("۲");
        TelegramFormats.Count(14286).Should().Be("۱۴٬۲۸۶");
    }
}
