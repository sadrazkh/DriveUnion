using System.Text;
using DriveUnion.Core.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

public class TelegramCallbackDataTests
{
    [Fact]
    public void A_file_button_fits_in_the_sixty_four_bytes_telegram_allows()
    {
        var data = TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, Guid.NewGuid());

        // Not a stylistic bound. Telegram refuses a send whose callback_data is 65 bytes, so a card
        // that exceeded it would simply fail to render — in production, on somebody's file.
        Encoding.UTF8.GetByteCount(data).Should().BeLessThanOrEqualTo(TelegramCallbackData.MaxBytes);

        // A GUID as text is 36 characters and two of them do not fit. Twenty-two-character base64url
        // of the sixteen bytes is what makes room for a verb and a second value.
        data.Should().HaveLength(24);
    }

    [Fact]
    public void Two_values_still_fit()
    {
        var data = TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, Guid.NewGuid(), long.MaxValue);

        Encoding.UTF8.GetByteCount(data).Should().BeLessThanOrEqualTo(TelegramCallbackData.MaxBytes);
    }

    [Fact]
    public void Every_verb_round_trips()
    {
        var id = Guid.NewGuid();

        foreach (var verb in Enum.GetValues<TelegramCallbackVerb>())
        {
            var decoded = TelegramCallbackData.Decode(TelegramCallbackData.Encode(verb, id, 42));

            decoded.Should().NotBeNull();
            decoded!.Verb.Should().Be(verb);
            decoded.Id.Should().Be(id);
            decoded.Number.Should().Be(42);
        }
    }

    [Fact]
    public void A_verb_with_only_a_number_round_trips()
    {
        // The delivered message's «دریافت کردم، پاک کن» carries a message id and no GUID.
        var decoded = TelegramCallbackData.Decode(
            TelegramCallbackData.Encode(TelegramCallbackVerb.AcknowledgeDelivery, null, 987654321));

        decoded.Should().NotBeNull();
        decoded!.Verb.Should().Be(TelegramCallbackVerb.AcknowledgeDelivery);
        decoded.Id.Should().BeNull();
        decoded.Number.Should().Be(987654321);
    }

    [Theory]
    [InlineData("")]
    [InlineData("z.AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("s.not-a-guid")]
    [InlineData("s.AAAAAAAAAAAAAAAAAAAAAA.not-a-number")]
    [InlineData("s.AAAAAAAAAAAAAAAAAAAAAA.1.2.3")]
    public void Anything_this_bot_did_not_mint_decodes_to_nothing(string data)
    {
        // A stale button, a crafted string, a value from another product's bot — all the same
        // answer. The handler treats null as "do nothing", because a callback is client-supplied and
        // an error message is the reply an abuser is trying to get.
        TelegramCallbackData.Decode(data).Should().BeNull();
    }

    [Fact]
    public void A_payload_longer_than_telegram_allows_is_refused_rather_than_parsed()
    {
        TelegramCallbackData.Decode(new string('s', TelegramCallbackData.MaxBytes + 1)).Should().BeNull();
    }
}
