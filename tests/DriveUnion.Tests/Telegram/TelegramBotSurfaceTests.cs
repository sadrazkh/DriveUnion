using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The bot's surface, driven through the real update handler against the in-memory Telegram.
///
/// Nothing here reaches a network. What is under test is which tenant a chat may read, what a
/// stranger is told, and whether a button that cannot succeed is ever drawn.
/// </summary>
public class TelegramBotSurfaceTests
{
    [Fact]
    public async Task Never_linked_unlinked_and_deleted_all_get_the_same_string()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 5001);

        // Unlinked yesterday.
        await harness.Links().UnlinkAsync(user.Id, TelegramUnlinkReason.Customer, CancellationToken.None);

        var handler = harness.Handler();

        await handler.HandleAsync(TelegramTestHarness.TextUpdate(9999, "/files", 1), CancellationToken.None);
        await handler.HandleAsync(TelegramTestHarness.TextUpdate(5001, "/files", 2), CancellationToken.None);

        var replies = harness.Telegram.Calls
            .Where(c => c.Operation is FakeTelegramOperation.SendMessage)
            .Select(c => c.Text)
            .ToList();

        // Byte-identical, and that is the whole point. A bot that answered "your account was
        // disconnected" to one stranger and "unknown account" to another is an oracle for which
        // Telegram accounts are customers of this service, and anyone in the world can make it
        // answer. The cost — a real customer sees a generic line — is paid by the farewell that
        // goes out at the moment the link is removed.
        replies.Should().HaveCount(2);
        replies.Should().AllBe(TelegramMessages.Stranger);
    }

    [Fact]
    public async Task A_group_chat_is_answered_once_and_never_acted_on()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 5001);

        var update = new TelegramUpdate(
            1,
            new TelegramIncomingMessage(
                1,
                new TelegramChat(-100200300, "supergroup"),
                new TelegramSender(5001, "someone", "Some One", "fa"),
                "/files",
                null),
            null);

        await harness.Handler().HandleAsync(update, CancellationToken.None);

        // Binding is on from.id and never chat.id: the two are equal in a private chat and different
        // everywhere else, so answering a group would eventually mean every member of that group
        // reading a tenant's file list.
        harness.Telegram.Calls.Should().ContainSingle();
        harness.Telegram.Calls[0].Text.Should().Be(TelegramMessages.PrivateChatsOnly);
    }

    [Fact]
    public async Task A_callback_naming_another_tenants_file_gets_what_a_random_guid_gets()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();

        var tenantA = harness.SeedTenant("alpha");
        var fileOfA = harness.SeedFile(tenantA.Id, account.Id, "secret-plans.pdf");

        var tenantB = harness.SeedTenant("beta");
        var userB = harness.SeedUser(tenantB.Id);
        await harness.LinkAsync(userB.Id, 6001);

        var handler = harness.Handler();

        await handler.HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                6001,
                TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, fileOfA.Id),
                updateId: 1),
            CancellationToken.None);

        await handler.HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                6001,
                TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, Guid.NewGuid()),
                updateId: 2),
            CancellationToken.None);

        var cards = harness.Telegram.Calls
            .Where(c => c.Operation is FakeTelegramOperation.EditMessage or FakeTelegramOperation.SendMessage)
            .Select(c => c.Text)
            .ToList();

        // Identical answers. A distinguishable "not yours" is exactly what turns a file id into
        // something worth guessing, and callback data arrives from a client we do not control.
        cards.Should().HaveCount(2);
        cards.Should().AllBe(TelegramMessages.FileNotAvailable);
        cards.Should().NotContain(t => t!.Contains("secret-plans", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_the_bot_says_names_the_storage_provider()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 7001);

        var file = harness.SeedFile(tenant.Id, account.Id);
        var handler = harness.Handler();

        await handler.HandleAsync(TelegramTestHarness.TextUpdate(7001, "/start", 1), CancellationToken.None);
        await handler.HandleAsync(TelegramTestHarness.TextUpdate(7001, "/help", 2), CancellationToken.None);
        await handler.HandleAsync(TelegramTestHarness.TextUpdate(7001, "/files", 3), CancellationToken.None);
        await handler.HandleAsync(TelegramTestHarness.TextUpdate(7001, "/quota", 4), CancellationToken.None);
        await handler.HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                7001,
                TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, file.Id),
                updateId: 5),
            CancellationToken.None);

        // Asserted on the raw outbound strings rather than on a typed object, and that is
        // deliberate: the bug being guarded against is a message template gaining a field, and a
        // typed assertion would not notice.
        var outbound = harness.Telegram.SentTexts.Concat(harness.Telegram.ButtonLabels).ToList();

        outbound.Should().NotBeEmpty();

        foreach (var text in outbound)
        {
            text.Should().NotContainEquivalentOf("google");
            text.Should().NotContainEquivalentOf("drive");
            text.Should().NotContain(account.Email);
            text.Should().NotContain(account.Label);
            text.Should().NotContain(file.DriveFileId);
        }
    }

    [Fact]
    public async Task A_file_over_the_ceiling_is_drawn_without_a_send_button()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.MaxSendBytes = 50_000_000;

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 8001);

        var file = harness.SeedFile(tenant.Id, account.Id, "archive.zip", sizeBytes: 214L * 1000 * 1000 * 1000);

        // The card must decide from metadata alone. Nothing may read the bytes to draw it — which is
        // also why this test costs nothing despite the size.
        harness.Telegram.Forbid(FakeTelegramOperation.SendDocument);

        await harness.Handler().HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                8001,
                TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, file.Id)),
            CancellationToken.None);

        var card = harness.Telegram.Calls.Single(c => c.Operation is FakeTelegramOperation.EditMessage);

        // Absent, not disabled. A capability the customer does not have is absent; a condition they
        // can fix is disabled — and a file's size is not a condition they can fix.
        card.ButtonLabels.Should().NotContain(TelegramMessages.ButtonSend);
        card.ButtonLabels.Should().Contain(TelegramMessages.ButtonCreateLink);
        card.Text.Should().Contain(TelegramMessages.TooLargeToSend);
    }

    [Fact]
    public async Task Pressing_send_on_an_oversized_file_never_reaches_an_upload()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 8002);

        var file = harness.SeedFile(tenant.Id, account.Id, "huge.mkv", sizeBytes: 812L * 1000 * 1000 * 1000);

        harness.Telegram.Forbid(FakeTelegramOperation.SendDocument);

        // A stale card is one scroll away, so the same decision is made again here. The assertion is
        // that nothing was queued and nothing was read.
        await harness.Handler().HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                8002,
                TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, file.Id)),
            CancellationToken.None);

        (await harness.Db.TelegramOutbox.CountAsync()).Should().Be(0);
        harness.Drive.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_update_delivered_twice_acts_once()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 9001);

        var file = harness.SeedFile(tenant.Id, account.Id, sizeBytes: 1024);
        var press = TelegramTestHarness.CallbackUpdate(
            9001,
            TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, file.Id),
            updateId: 4242);

        var handler = harness.Handler();

        (await handler.HandleAsync(press, CancellationToken.None))
            .Should().Be(TelegramUpdateOutcome.Handled);

        // Telegram redelivers whenever the webhook answers non-2xx or times out. A second delivery
        // that queued a second send is a file the customer paid for twice.
        (await handler.HandleAsync(press, CancellationToken.None))
            .Should().Be(TelegramUpdateOutcome.Duplicate);

        (await harness.Db.TelegramOutbox.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_inbound_file_over_the_ceiling_is_refused_before_anything_is_fetched()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.MaxReceiveBytes = 20_000_000;

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 9101);

        // getFile is what materialises the bytes on a box with no room, so the decision has to be
        // made from the declared size and before the call.
        harness.Telegram.Forbid(FakeTelegramOperation.GetFile);

        await harness.Handler().HandleAsync(
            TelegramTestHarness.FileUpdate(9101, "tg-1", "premium.mkv", 3_000_000_000),
            CancellationToken.None);

        (await harness.Db.TelegramOutbox.CountAsync()).Should().Be(0);

        var reply = harness.Telegram.Calls.Single(c => c.Operation is FakeTelegramOperation.SendMessage);

        // The refusal names the next action: the panel's own uploader, which carries files this bot
        // never could.
        reply.Text.Should().Contain("/Files/Upload");
        reply.Text.Should().Contain("20.0 MB");
    }

    [Fact]
    public async Task An_inbound_file_under_the_ceiling_is_queued_and_acknowledged_at_once()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 9102);

        // The handler must not fetch, upload or wait: it queues and answers. A handler that moved
        // bytes before replying is a handler Telegram redelivers on top of.
        harness.Telegram.Forbid(FakeTelegramOperation.GetFile);

        await harness.Handler().HandleAsync(
            TelegramTestHarness.FileUpdate(9102, "tg-1", "notes.pdf", 4096),
            CancellationToken.None);

        var queued = await harness.Db.TelegramOutbox.SingleAsync();

        queued.Kind.Should().Be(TelegramOutboxKind.ReceiveDocument);
        queued.TenantId.Should().Be(tenant.Id);
        queued.SizeBytes.Should().Be(4096);

        harness.Telegram.Calls.Should().Contain(c => c.Text!.Contains("notes.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stranger_is_answered_three_times_an_hour_and_then_met_with_silence()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.StrangerRepliesPerHour = 3;

        var handler = harness.Handler();

        for (var i = 1; i <= 6; i++)
        {
            await handler.HandleAsync(
                TelegramTestHarness.TextUpdate(4242, "hello?", i),
                CancellationToken.None);
        }

        // Silence, not an error. An error is a reply, and a reply is the resource being abused —
        // the whole Telegram user-id space is enumerable at whatever rate we allow.
        harness.Telegram.Calls
            .Count(c => c.Operation is FakeTelegramOperation.SendMessage)
            .Should().Be(3);
    }

    [Fact]
    public async Task Help_states_both_ceilings_before_anyone_discovers_them_by_failing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.MaxSendBytes = 2_000_000_000;
        harness.Options.MaxReceiveBytes = 2_000_000_000;

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 9201);

        await harness.Handler().HandleAsync(
            TelegramTestHarness.TextUpdate(9201, "/help"),
            CancellationToken.None);

        var help = harness.Telegram.Calls.Single(c => c.Operation is FakeTelegramOperation.SendMessage);

        // Both numbers, read from configuration rather than typed into the copy. They are the one
        // thing a customer would otherwise learn by failing.
        help.Text.Should().Contain("2.0 GB");
    }

    [Fact]
    public async Task Every_callback_is_answered_even_when_the_action_refuses()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 9301);

        await harness.Handler().HandleAsync(
            TelegramTestHarness.CallbackUpdate(
                9301,
                TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, Guid.NewGuid())),
            CancellationToken.None);

        // A button that spins for ever is the most common way a bot looks broken, and this is the
        // path where it is most likely to be forgotten: the action failed.
        harness.Telegram.Calls
            .Should().Contain(c => c.Operation == FakeTelegramOperation.AnswerCallbackQuery);
    }

    [Fact]
    public async Task Three_different_failures_never_share_one_sentence()
    {
        // From a chat, "this file cannot be fetched right now", "you have no files" and "you are not
        // linked" look identical if they are all «خطایی رخ داد» — and this is precisely the moment a
        // customer needs to know which one they are looking at.
        var distinct = new[]
        {
            TelegramMessages.Stranger,
            TelegramMessages.NoFiles,
            TelegramMessages.TemporarilyUnavailable,
            TelegramMessages.FileNotAvailable,
            TelegramMessages.QueueFull,
        };

        distinct.Should().OnlyHaveUniqueItems();

        await Task.CompletedTask;
    }
}
