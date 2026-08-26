using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The queue and its drainer, driven directly rather than through the hosted service around it.
///
/// The loop is deliberately thin; everything worth arguing about — fairness, the transfer slot, the
/// free-space pre-flight, what a 429 costs — is in the processor, which is what these construct.
/// </summary>
public class TelegramOutboxTests
{
    [Fact]
    public async Task The_drain_runs_with_no_request_and_asserts_it_did_something()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "سلام" }),
            0,
            null,
            CancellationToken.None);

        // No HttpContext, no cookie, no principal: the TenantId on the row is the whole of the
        // drainer's tenant identity. The assertion is deliberately on a NON-EMPTY result, because
        // the bug this guards against — a sessionless worker reading an empty database and reporting
        // success — has an empty one as its entire signature.
        var drained = await harness.Processor().DrainOnceAsync(CancellationToken.None);

        drained.Should().Be(1);
        harness.Telegram.Calls.Should().ContainSingle(c => c.Operation == FakeTelegramOperation.SendMessage);

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();
        row.Status.Should().Be(TelegramOutboxStatus.Sent);
    }

    [Fact]
    public async Task One_tenants_backlog_does_not_become_every_other_tenants_latency()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var noisy = harness.SeedTenant("noisy");
        var quiet = harness.SeedTenant("quiet");

        harness.Options.MaxQueuedPerTenant = 200;

        var outbox = harness.Outbox();

        for (var i = 0; i < 100; i++)
        {
            await outbox.EnqueueAsync(
                noisy.Id,
                senderUserId: null,
                1000,
                TelegramOutboxKind.SendMessage,
                null,
                JsonSerializer.Serialize(new TelegramOutboxPayload { Text = $"noisy {i}" }),
                0,
                null,
                CancellationToken.None);
        }

        await outbox.EnqueueAsync(
            quiet.Id,
            senderUserId: null,
            2000,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "quiet" }),
            0,
            null,
            CancellationToken.None);

        var processor = harness.Processor();
        var order = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var claimed = await processor.ClaimNextAsync(movesBytes: false, CancellationToken.None);
            claimed.Should().NotBeNull();
            order.Add(claimed!.TenantId);

            await processor.ExecuteAsync(claimed, CancellationToken.None);
        }

        // Not FIFO. The shared resource is a single bot identity with one global ceiling, so one
        // tenant's backlog is directly every other tenant's latency from the second tenant onwards —
        // the quiet tenant's one item must not be a hundred sends away.
        order.Should().Contain(quiet.Id);
        order.IndexOf(quiet.Id).Should().BeLessThan(3);
    }

    [Fact]
    public async Task A_transfer_never_blocks_a_chat_reply_behind_it()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(tenant.Id, account.Id, sizeBytes: 4096, content: FakeTelegramBotGateway.TestBytes(4096));

        var outbox = harness.Outbox();

        // Three deliveries queued first, then the reply.
        for (var i = 0; i < 3; i++)
        {
            await outbox.EnqueueAsync(
                tenant.Id,
                senderUserId: null,
                1000,
                TelegramOutboxKind.SendDocument,
                file.Id,
                null,
                4096,
                null,
                CancellationToken.None);
        }

        await outbox.EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            1000,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "پاسخ کوتاه" }),
            0,
            null,
            CancellationToken.None);

        var processor = harness.Processor();

        // The short lane is claimed separately from the transfer lane, so the reply comes out first
        // even though it was queued last. Without the split, the messages that explain what is
        // happening are stuck behind the transfers causing it.
        var first = await processor.ClaimNextAsync(movesBytes: false, CancellationToken.None);

        first.Should().NotBeNull();
        first!.Kind.Should().Be(TelegramOutboxKind.SendMessage);
    }

    [Fact]
    public async Task Flood_control_parks_the_item_and_does_not_spend_an_attempt()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "hello" }),
            0,
            null,
            CancellationToken.None);

        harness.Telegram.ThrottleNext(FakeTelegramOperation.SendMessage, TimeSpan.FromSeconds(37));

        var processor = harness.Processor();
        var item = await processor.ClaimNextAsync(movesBytes: false, CancellationToken.None);
        await processor.ExecuteAsync(item!, CancellationToken.None);

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();

        // A 429 is obeyed, not retried. Parking until the instant Telegram named and leaving the
        // attempt budget alone is the difference between a backlog that clears and one that fails
        // for a reason no user could understand.
        row.Status.Should().Be(TelegramOutboxStatus.Pending);
        row.Attempt.Should().Be(0);
        row.NextAttemptAt.Should().Be(TelegramTestHarness.Now.AddSeconds(37));
    }

    [Fact]
    public async Task A_parked_item_is_not_claimed_before_its_moment()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "later" }),
            0,
            TelegramTestHarness.Now.AddMinutes(30),
            CancellationToken.None);

        var processor = harness.Processor();

        (await processor.ClaimNextAsync(movesBytes: false, CancellationToken.None)).Should().BeNull();

        harness.Clock.Advance(TimeSpan.FromMinutes(31));

        (await processor.ClaimNextAsync(movesBytes: false, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_byte_moving_item_gets_the_shorter_attempt_budget()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.MaxTransferAttempts = 3;

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            null,
            4096,
            null,
            CancellationToken.None);

        // A 500 is retryable, so it spends the budget rather than ending the item outright.
        harness.Telegram.FailAlways(
            FakeTelegramOperation.SendDocument,
            new TelegramFailure(500, "Internal Server Error"));

        var processor = harness.Processor();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var item = await processor.ClaimNextAsync(movesBytes: true, CancellationToken.None);
            item.Should().NotBeNull();

            await processor.ExecuteAsync(item!, CancellationToken.None);
            harness.Clock.Advance(TimeSpan.FromHours(1));
        }

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();

        // Three, not five. Every attempt at a ceiling-sized delivery costs its own size twice over —
        // once read out of storage, once pushed to the server — so five attempts on a failing two
        // gigabytes is twenty gigabytes of reads and egress for something that has already failed
        // four times.
        row.Attempt.Should().Be(3);
        row.Status.Should().Be(TelegramOutboxStatus.Failed);
    }

    [Fact]
    public async Task A_locked_file_is_refused_and_the_card_says_why()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();

        // Deliberately readable: the point is that nothing reads it. A fixture with no bytes behind
        // it would pass this test even if the refusal were removed.
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            "passport.pdf",
            content: FakeTelegramBotGateway.TestBytes(64));

        harness.Db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = tenant.Id,
            Scheme = 1,
            SegmentSize = 1024 * 1024,
            NoncePrefix = "AAAAAAAAAAA=",
            PlaintextLength = 48,
            KdfSalt = "BBBBBBBBBBBBBBBBBBBBBB==",
            KdfIterations = 600_000,
            WrappedKey = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=",
            CreatedAt = TelegramTestHarness.Now,
        });
        await harness.Db.SaveChangesAsync();

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            null,
            file.SizeBytes,
            null,
            CancellationToken.None);

        await harness.Processor().DrainOnceAsync(CancellationToken.None);

        // Nothing was read out of storage and nothing was sent. This bot cannot decrypt and never
        // will — the key is in somebody's browser — so a document delivered into a chat would be
        // ciphertext wearing the right name, which is the one failure a program cannot detect.
        harness.Drive.Calls.Should().BeEmpty();
        harness.Telegram.Calls.Should().NotContain(c => c.Operation == FakeTelegramOperation.SendDocument);

        // Permanent, not parked: a retry would find the same file, still encrypted.
        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();
        row.Status.Should().Be(TelegramOutboxStatus.Failed);

        // And the customer is told what to do instead, which is the one thing that does work.
        harness.Telegram.Calls
            .Should().Contain(c => c.Text != null && c.Text.Contains(TelegramMessages.FileIsLocked));
    }

    [Fact]
    public async Task The_pre_flight_refuses_before_anything_is_read_out_of_storage()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.WorkDirectory = harness.Telegram.WorkDirectory;
        harness.Options.WorkDirHeadroomBytes = 1_000_000_000;
        harness.Options.WorkDirMinFreeBytes = 100;

        // The volume cannot hold the file plus headroom.
        harness.Disk.FreeBytes = 500_000_000;

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 900_000_000,
            content: FakeTelegramBotGateway.TestBytes(16));

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            null,
            900_000_000,
            null,
            CancellationToken.None);

        harness.Telegram.Forbid(FakeTelegramOperation.SendDocument);

        var claimed = await harness.Processor().ClaimNextAsync(movesBytes: true, CancellationToken.None);

        // Nothing was claimed, so nothing was read. Beginning a transfer onto a volume that cannot
        // hold it fails at ninety-eight per cent, having done all the work, read all the bytes out of
        // storage, and filled the disk on the way out.
        claimed.Should().BeNull();
        harness.Drive.Calls.Should().BeEmpty();

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();
        row.Status.Should().Be(TelegramOutboxStatus.Pending);
    }

    [Fact]
    public async Task Room_on_the_volume_lets_the_same_item_through()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.WorkDirectory = harness.Telegram.WorkDirectory;
        harness.Options.WorkDirHeadroomBytes = 1_000;
        harness.Options.WorkDirMinFreeBytes = 1_000;
        harness.Disk.FreeBytes = 10_000_000;

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            null,
            4096,
            null,
            CancellationToken.None);

        (await harness.Processor().ClaimNextAsync(movesBytes: true, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_delivery_caches_the_handle_and_the_next_one_reads_nothing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        var outbox = harness.Outbox();
        var processor = harness.Processor();

        await outbox.EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var cached = await harness.Db.TelegramFileIds.AsNoTracking().SingleAsync();
        cached.StoredFileId.Should().Be(file.Id);
        cached.BotUserId.Should().Be(123456789);

        harness.Drive.Calls.Should().NotBeEmpty();
        var readsAfterFirst = harness.Drive.Calls.Count;

        await outbox.EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // No storage read at all on the second delivery, no bytes off this box, and no working
        // directory. It is the single largest performance decision in this slice and it is free.
        harness.Drive.Calls.Should().HaveCount(readsAfterFirst);

        var sends = harness.Telegram.Calls
            .Where(c => c.Operation is FakeTelegramOperation.SendDocument)
            .ToList();

        sends.Should().HaveCount(2);
        sends[0].CachedFileId.Should().BeNull();
        sends[0].UploadedBytes.Should().Be(4096);
        sends[1].CachedFileId.Should().Be(cached.FileId);
        sends[1].UploadedBytes.Should().Be(0);
    }

    [Fact]
    public async Task A_cached_handle_from_another_bot_is_a_miss_and_never_a_wrong_send()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        // A handle minted against a different bot. A file_id cannot be transferred between bots, so
        // pointing the panel at another token must produce a cache MISS — a wrong send is the one
        // outcome the key was designed to prevent.
        harness.Db.TelegramFileIds.Add(new TelegramFileId
        {
            StoredFileId = file.Id,
            BotUserId = 999_999_999,
            FileId = "handle-from-the-other-bot",
            FileUniqueId = "u",
            SizeBytes = 4096,
            CachedAt = TelegramTestHarness.Now,
        });
        await harness.Db.SaveChangesAsync();

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var send = harness.Telegram.Calls.Single(c => c.Operation is FakeTelegramOperation.SendDocument);

        send.CachedFileId.Should().NotBe("handle-from-the-other-bot");
        send.UploadedBytes.Should().Be(4096);
    }

    [Fact]
    public async Task A_delivery_whose_file_was_deleted_says_so_rather_than_retrying()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(tenant.Id, account.Id, sizeBytes: 4096);

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        // The customer deleted it between the press and the drain. The queue holds no foreign key on
        // purpose: the delete must not be refused, and the row must not vanish under the drainer.
        await harness.Db.StoredFiles
            .Where(f => f.Id == file.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, TelegramTestHarness.Now));

        harness.Telegram.Forbid(FakeTelegramOperation.SendDocument);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();
        row.Status.Should().Be(TelegramOutboxStatus.Failed);

        harness.Telegram.SentTexts.Should().Contain(TelegramMessages.FileNotAvailable);
    }

    [Fact]
    public async Task A_delete_message_item_survives_a_restart_because_the_queue_is_the_timer()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.DeliveryMessageTtlMinutes = 30;

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var deletion = await harness.Db.TelegramOutbox
            .AsNoTracking()
            .SingleAsync(o => o.Kind == TelegramOutboxKind.DeleteMessage);

        // The lifetime is a queued row with a deadline, not an in-memory timer — an in-memory one
        // does not survive the restart a deploy makes routine, and «پیامش پاک بشه» failing silently
        // after a release is exactly the failure this shape refuses.
        deletion.NextAttemptAt.Should().Be(TelegramTestHarness.Now.AddMinutes(30));

        harness.Clock.Advance(TimeSpan.FromMinutes(31));

        // A freshly constructed processor — the "restart" — picks it up and carries it out.
        var afterRestart = harness.Processor();
        var claimed = await afterRestart.ClaimNextAsync(movesBytes: false, CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.Kind.Should().Be(TelegramOutboxKind.DeleteMessage);

        await afterRestart.ExecuteAsync(claimed, CancellationToken.None);

        harness.Telegram.Calls
            .Should().Contain(c => c.Operation == FakeTelegramOperation.DeleteMessage);
    }

    [Fact]
    public async Task A_delivered_message_offers_the_button_and_no_timer_by_default()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        harness.Options.DeliveryMessageTtlMinutes.Should().Be(0);

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            JsonSerializer.Serialize(new TelegramOutboxPayload { CardMessageId = 77 }),
            4096,
            null,
            CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // The button is the feature; the timer is off by default, because the delivered document
        // sitting in the customer's own chat is the one genuine second copy in this design and a
        // timer deletes it.
        harness.Telegram.ButtonLabels.Should().Contain(TelegramMessages.ButtonAcknowledge);

        (await harness.Db.TelegramOutbox.CountAsync(o => o.Kind == TelegramOutboxKind.DeleteMessage))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_long_upload_keeps_the_chat_from_looking_dead()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: 4096,
            content: FakeTelegramBotGateway.TestBytes(4096));

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // A chat action lasts about five seconds, so a four-minute upload that sent one at the start
        // would look idle for three minutes and fifty-five — the exact appearance of a broken bot.
        harness.Telegram.Calls
            .Should().Contain(c => c.Operation == FakeTelegramOperation.SendChatAction);
    }

    [Fact]
    public async Task The_queue_is_bounded_by_items_and_by_bytes()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        harness.Options.MaxQueuedPerTenant = 3;
        harness.Options.MaxQueuedBytesPerTenant = 10_000;

        var outbox = harness.Outbox();

        for (var i = 0; i < 3; i++)
        {
            (await outbox.EnqueueAsync(
                    tenant.Id, senderUserId: null, 1, TelegramOutboxKind.SendMessage, null, null, 0, null, CancellationToken.None))
                .Status.Should().Be(TelegramEnqueueStatus.Queued);
        }

        (await outbox.EnqueueAsync(
                tenant.Id, senderUserId: null, 1, TelegramOutboxKind.SendMessage, null, null, 0, null, CancellationToken.None))
            .Status.Should().Be(TelegramEnqueueStatus.QueueFull);
    }

    [Fact]
    public async Task The_byte_bound_bites_before_the_item_bound_does()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        harness.Options.MaxQueuedPerTenant = 50;
        harness.Options.MaxQueuedBytesPerTenant = 4_000_000_000;

        var outbox = harness.Outbox();

        // Two ceiling-sized deliveries fit; the third is a hundred gigabytes of pending work away
        // from anything a customer would call a queue. A bound in items only is not a bound.
        for (var i = 0; i < 2; i++)
        {
            (await outbox.EnqueueAsync(
                    tenant.Id,
                    senderUserId: null,
                    1,
                    TelegramOutboxKind.SendDocument,
                    Guid.NewGuid(),
                    null,
                    2_000_000_000,
                    null,
                    CancellationToken.None))
                .Status.Should().Be(TelegramEnqueueStatus.Queued);
        }

        (await outbox.EnqueueAsync(
                tenant.Id,
                senderUserId: null,
                1,
                TelegramOutboxKind.SendDocument,
                Guid.NewGuid(),
                null,
                2_000_000_000,
                null,
                CancellationToken.None))
            .Status.Should().Be(TelegramEnqueueStatus.QueueFull);
    }

    [Fact]
    public async Task A_deletion_is_never_refused_for_being_over_a_bound()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        harness.Options.MaxQueuedPerTenant = 1;

        var outbox = harness.Outbox();

        await outbox.EnqueueAsync(
            tenant.Id, senderUserId: null, 1, TelegramOutboxKind.SendMessage, null, null, 0, null, CancellationToken.None);

        // It is the item that makes the chat smaller, it moves no bytes, and refusing it would leave
        // a document sitting in a chat the customer asked to have cleaned up.
        (await outbox.EnqueueAsync(
                tenant.Id,
                senderUserId: null,
                1,
                TelegramOutboxKind.DeleteMessage,
                null,
                JsonSerializer.Serialize(new TelegramOutboxPayload { MessageId = 5 }),
                0,
                null,
                CancellationToken.None))
            .Status.Should().Be(TelegramEnqueueStatus.Queued);
    }

    [Fact]
    public async Task A_blocked_customer_stops_the_queue_retrying_into_a_wall()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(user.Id, 5001);

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "hello" }),
            0,
            null,
            CancellationToken.None);

        harness.Telegram.FailAlways(
            FakeTelegramOperation.SendMessage,
            new TelegramFailure(403, "Forbidden: bot was blocked by the user"));

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(false, CancellationToken.None))!,
            CancellationToken.None);

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();
        row.Status.Should().Be(TelegramOutboxStatus.Failed);

        var account = await harness.Db.TelegramAccounts.AsNoTracking().SingleAsync();

        // Surfaced on the customer's own settings card, which is the only place the fact is useful:
        // the fix is on their phone and nowhere else.
        account.DeliveryStatus.Should().Be(TelegramDeliveryStatus.Blocked);
    }

    [Fact]
    public async Task Telegrams_own_words_are_kept_verbatim()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendMessage,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload { Text = "hello" }),
            0,
            null,
            CancellationToken.None);

        harness.Telegram.FailAlways(
            FakeTelegramOperation.SendMessage,
            new TelegramFailure(400, "Bad Request: message text is empty"));

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(false, CancellationToken.None))!,
            CancellationToken.None);

        var row = await harness.Db.TelegramOutbox.AsNoTracking().SingleAsync();

        // Not classified into our own vocabulary. The classifier is meant to be tightened in the
        // first week from real log lines rather than from a mapping guessed in advance, and a
        // paraphrase throws away the only diagnosis there is.
        row.ErrorDetail.Should().Be("Bad Request: message text is empty");
        row.ErrorCode.Should().Be("400");
    }

    [Fact]
    public async Task An_item_a_process_died_holding_comes_back_to_the_queue()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendMessage, null, null, 0, null, CancellationToken.None);

        var claimed = await harness.Processor().ClaimNextAsync(false, CancellationToken.None);
        claimed.Should().NotBeNull();

        // Nothing else claims it while it is plausibly still running.
        (await TelegramSweeperService.RecoverStaleClaimsAsync(
            harness.Db, harness.Clock, CancellationToken.None))
            .Should().Be(0);

        harness.Clock.Advance(TelegramSweeperService.StaleClaim + TimeSpan.FromMinutes(1));

        // Past the point where it can be running, it goes back. Without this a deploy landing
        // mid-transfer leaves the row Claimed for ever: nothing retries it, nothing reports it, and
        // the customer's file simply never arrives.
        (await TelegramSweeperService.RecoverStaleClaimsAsync(
            harness.Db, harness.Clock, CancellationToken.None))
            .Should().Be(1);

        (await harness.Processor(harness.NewContext()).ClaimNextAsync(false, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task An_item_is_claimed_once_even_when_two_drainers_race_for_it()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendMessage, null, null, 0, null, CancellationToken.None);

        var first = await harness.Processor().ClaimNextAsync(false, CancellationToken.None);
        var second = await harness.Processor(harness.NewContext()).ClaimNextAsync(false, CancellationToken.None);

        // The claim is a conditional UPDATE and its rows-affected is the arbiter. Two drainers, or
        // two loops in one, must not both carry out the same send.
        first.Should().NotBeNull();
        second.Should().BeNull();
    }
}
