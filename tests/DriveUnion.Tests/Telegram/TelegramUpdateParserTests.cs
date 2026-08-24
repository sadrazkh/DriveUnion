using System.Text.Json;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The webhook body, turned into an update or into nothing.
///
/// This is the one place in the slice that reads a shape somebody else controls, and the endpoint it
/// feeds is anonymous — so what has to be true is that it is tolerant of fields it does not know and
/// intolerant of the ones it needs.
/// </summary>
public class TelegramUpdateParserTests
{
    private readonly TelegramUpdateParser _parser = new();

    [Fact]
    public void A_private_text_message_reads_the_sender_rather_than_the_chat()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 42,
            message = new
            {
                message_id = 7,
                chat = new { id = 5001, type = "private" },
                from = new
                {
                    id = 5001,
                    first_name = "Some",
                    last_name = "One",
                    username = "someone",
                    language_code = "fa",
                },
                text = "/files",
            },
        }));

        update.Should().NotBeNull();
        update!.UpdateId.Should().Be(42);
        update.Message!.Chat.IsPrivate.Should().BeTrue();
        update.Message.From!.Id.Should().Be(5001);
        update.Message.From.DisplayName.Should().Be("Some One");
        update.Message.From.LanguageCode.Should().Be("fa");
        update.Message.Text.Should().Be("/files");
    }

    [Fact]
    public void A_photo_is_read_at_its_original_size()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 1,
            message = new
            {
                message_id = 7,
                chat = new { id = 5001, type = "private" },
                from = new { id = 5001, first_name = "Some" },
                photo = new object[]
                {
                    new { file_id = "thumb", file_unique_id = "t", file_size = 900 },
                    new { file_id = "original", file_unique_id = "o", file_size = 400000 },
                },
            },
        }));

        // Telegram sends a photo as an array of sizes, smallest first. Anything but the last
        // silently downgrades the customer's own picture on its way into their storage.
        update!.Message!.File!.FileId.Should().Be("original");
        update.Message.File.FileSize.Should().Be(400000);
        update.Message.File.FileName.Should().Be("photo.jpg");
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("voice")]
    [InlineData("animation")]
    public void The_other_file_fields_are_the_same_concept(string field)
    {
        var json = $$"""
        {
          "update_id": 1,
          "message": {
            "message_id": 7,
            "chat": { "id": 5001, "type": "private" },
            "from": { "id": 5001, "first_name": "Some" },
            "{{field}}": { "file_id": "f", "file_unique_id": "u", "file_size": 1234 }
          }
        }
        """;

        // A bot that only understood "document" would silently ignore every video anybody sent it —
        // which from the chat looks exactly like a bot that is broken.
        _parser.Parse(json)!.Message!.File!.FileId.Should().Be("f");
    }

    [Fact]
    public void A_file_with_no_declared_size_reads_as_unknown_rather_than_as_zero()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 1,
            message = new
            {
                message_id = 7,
                chat = new { id = 5001, type = "private" },
                from = new { id = 5001, first_name = "Some" },
                document = new { file_id = "f", file_unique_id = "u", file_name = "notes.pdf" },
            },
        }));

        // file_size is optional in the API. Absent has to be unknown and not zero, or an
        // undeclared two-gigabyte file passes every ceiling check there is.
        update!.Message!.File!.FileSize.Should().BeNull();
    }

    [Fact]
    public void An_edited_message_is_handled_as_the_message_it_became()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 1,
            edited_message = new
            {
                message_id = 7,
                chat = new { id = 5001, type = "private" },
                from = new { id = 5001, first_name = "Some" },
                text = "/quota",
            },
        }));

        // A customer correcting a typo in a command is asking for the corrected command, and
        // treating the edit as nothing is a chat that stops answering for no visible reason.
        update!.Message!.Text.Should().Be("/quota");
    }

    [Fact]
    public void A_callback_query_carries_the_message_it_hangs_off()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 9,
            callback_query = new
            {
                id = "cb-1",
                from = new { id = 5001, first_name = "Some" },
                message = new
                {
                    message_id = 12,
                    chat = new { id = 5001, type = "private" },
                },
                data = "s.AAAAAAAAAAAAAAAAAAAAAA",
            },
        }));

        update!.CallbackQuery!.Id.Should().Be("cb-1");
        update.CallbackQuery.MessageId.Should().Be(12);
        update.CallbackQuery.Chat!.IsPrivate.Should().BeTrue();
        update.CallbackQuery.Data.Should().Be("s.AAAAAAAAAAAAAAAAAAAAAA");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"message\":{\"message_id\":1}}")]
    public void Anything_that_is_not_an_update_is_nothing(string body)
    {
        // The endpoint this feeds is anonymous and reachable by anything on the box, so a body that
        // is not JSON, is JSON but not an update, or is an update with no update_id all have to be a
        // null and a 200 rather than an exception page.
        _parser.Parse(body).Should().BeNull();
    }

    [Fact]
    public void Fields_this_product_does_not_know_are_ignored_rather_than_fatal()
    {
        var update = _parser.Parse("""
        {
          "update_id": 3,
          "message": {
            "message_id": 7,
            "chat": { "id": 5001, "type": "private", "some_future_field": true },
            "from": { "id": 5001, "first_name": "Some", "is_premium": true },
            "text": "/help",
            "reply_markup": { "inline_keyboard": [] }
          },
          "some_update_kind_that_does_not_exist_yet": { "a": 1 }
        }
        """);

        update!.Message!.Text.Should().Be("/help");
    }

    [Fact]
    public void A_group_chat_reads_as_one_and_is_the_handlers_problem_rather_than_the_parsers()
    {
        var update = _parser.Parse(JsonSerializer.Serialize(new
        {
            update_id = 1,
            message = new
            {
                message_id = 7,
                chat = new { id = -100200300, type = "supergroup" },
                from = new { id = 5001, first_name = "Some" },
                text = "/files",
            },
        }));

        update!.Message!.Chat.IsPrivate.Should().BeFalse();
        update.Message.Chat.Id.Should().NotBe(update.Message.From!.Id);
    }
}
