using System.Text;
using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Telegram;
using DriveUnion.Core.Uploads;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// Bytes in both directions, through the real coordinator and the real catalogue against the two
/// in-memory backends.
/// </summary>
public class TelegramFileFlowTests
{
    private const long BigEnoughToChunk = 20L * 1024 * 1024;

    [Fact]
    public async Task An_inbound_file_arrives_as_a_local_path_by_default_and_lands_in_storage()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(4096);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        // The fake answers with an absolute local path unless a test opts out, because that is the
        // branch production runs. The reverse arrangement is how a production-only bug gets written
        // and then passes every test.
        harness.Telegram.AnswerFilesWithUrls.Should().BeFalse();
        harness.Telegram.Forbid(FakeTelegramOperation.OpenRemoteFile);

        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var stored = await harness.Db.StoredFiles.AsNoTracking().SingleAsync();
        stored.TenantId.Should().Be(tenant.Id);
        stored.SizeBytes.Should().Be(content.LongLength);

        harness.Drive.Files.Values.Single().Content.Should().Equal(content);

        // The bytes exist on this box the moment getFile answers, so deleting them is our
        // obligation. There is no waiting period: the instant the send returns, the file is gone.
        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);
    }

    [Fact]
    public async Task The_url_branch_is_live_too_because_development_runs_it()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(4096);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        harness.Telegram.AnswerFilesWithUrls = true;

        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // Neither branch may be unreachable: production runs one and development runs the other, and
        // a branch that is only a comment about the other is a branch that has never worked.
        harness.Telegram.Calls
            .Should().Contain(c => c.Operation == FakeTelegramOperation.OpenRemoteFile);

        harness.Drive.Files.Values.Single().Content.Should().Equal(content);

        // Nothing was written here, so there is nothing to delete.
        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);
    }

    [Fact]
    public async Task The_local_copy_is_deleted_when_the_upload_fails_too()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(4096);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        harness.Drive.FailAlways(
            FakeDriveOperation.BeginResumableUpload,
            new DriveApiException("the storage session could not be opened"));

        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // Asserted on the filesystem rather than on a mock, and that is the whole point of the
        // assertion: deletion is a `finally` and a mock cannot tell a `finally` from an `if`. The
        // failure path is the one that leaves gigabytes behind.
        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);

        (await harness.Db.StoredFiles.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_that_lies_about_its_size_is_stopped_by_the_real_length()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        harness.Options.MaxReceiveBytes = 1000;

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(4096);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        // The declared size said 512 and the file is four kilobytes. A declared size is a claim, and
        // here the claim comes from a third party — so the queue is gated on it and the bytes are
        // checked again against what is actually there.
        await QueueInboundAsync(harness, tenant.Id, fileId, 512);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        (await harness.Db.StoredFiles.AsNoTracking().AnyAsync()).Should().BeFalse();
        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);

        harness.Telegram.SentTexts.Should().Contain(t => t.Contains("/Files/Upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_byte_counter_still_guards_the_branch_that_has_no_real_length()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(4096);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        harness.Telegram.AnswerFilesWithUrls = true;

        // Under the ceiling at queue time, over it by the time the copy runs. On the URL branch there
        // is no FileInfo.Length to check, so the counter on the copy is the only defence — which is
        // why it stays even though the local branch has a better check available.
        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);
        harness.Options.MaxReceiveBytes = 1000;

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        (await harness.Db.StoredFiles.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_too_large_for_one_chunk_is_written_in_aligned_ranges()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        harness.Options.MaxReceiveBytes = 2_000_000_000;

        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes((int)BigEnoughToChunk);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var chunks = harness.Drive.Calls
            .Where(c => c.Operation is FakeDriveOperation.WriteChunk)
            .ToList();

        chunks.Should().HaveCountGreaterThan(1, "twenty megabytes is no longer one final chunk");

        // Google requires every chunk but the last to be a multiple of 256 KiB. Violating it does not
        // fail loudly — the session simply stops acknowledging bytes, which reads like a stalled
        // network — so it is asserted here rather than discovered in production.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            (chunk.Length % UploadChunking.DriveChunkMultiple).Should().Be(0);
        }

        harness.Drive.Files.Values.Single().Content.Should().Equal(content);
        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);
    }

    [Fact]
    public async Task An_inbound_file_the_pool_cannot_take_gets_the_tenants_wording()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        // No pool account at all, which is what the coordinator refuses on.
        var tenant = harness.SeedTenant();
        var content = FakeTelegramBotGateway.TestBytes(1024);
        var fileId = harness.Telegram.SeedIncomingFile(content);

        await QueueInboundAsync(harness, tenant.Id, fileId, content.LongLength);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // The tenant-facing string, never the operator one that names how many accounts there are or
        // which of them are blocked.
        harness.Telegram.SentTexts.Should().Contain(TelegramMessages.UploadUnavailable);

        foreach (var text in harness.Telegram.SentTexts)
        {
            text.Should().NotContainEquivalentOf("google");
            text.Should().NotContainEquivalentOf("account");
        }

        harness.Telegram.FilesLeftInWorkDirectory().Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_reads_the_bytes_and_forwards_them_without_buffering()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var account = harness.SeedAccount();
        var tenant = harness.SeedTenant();
        var content = Encoding.UTF8.GetBytes("the quarterly report, in full");
        var file = harness.SeedFile(
            tenant.Id,
            account.Id,
            sizeBytes: content.LongLength,
            content: content);

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            senderUserId: null,
            5001,
            TelegramOutboxKind.SendDocument,
            file.Id,
            null,
            content.LongLength,
            null,
            CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var send = harness.Telegram.Calls.Single(c => c.Operation is FakeTelegramOperation.SendDocument);

        // Every byte reached Telegram, and the fake drained the stream itself — so a caller that had
        // already read the body would be caught here rather than in production on the one file big
        // enough to matter.
        send.UploadedBytes.Should().Be(content.LongLength);
        send.Text.Should().Be(file.Name);
    }

    [Fact]
    public async Task A_delivery_whose_storage_read_fails_says_something_true()
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

        harness.Drive.FailAlways(
            FakeDriveOperation.OpenDownload,
            new DriveApiException("the storage service answered 503"));

        await harness.Outbox().EnqueueAsync(
            tenant.Id, senderUserId: null, 5001, TelegramOutboxKind.SendDocument, file.Id, null, 4096, null, CancellationToken.None);

        harness.Telegram.Forbid(FakeTelegramOperation.SendDocument);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        // The customer needs to be able to tell "this cannot be fetched right now" from "you are
        // over the ceiling", and the failure lands on the card with a way forward rather than as a
        // bare error.
        var failure = harness.Telegram.Calls.Last(c =>
            c.Operation is FakeTelegramOperation.SendMessage or FakeTelegramOperation.EditMessage);

        failure.Text.Should().Contain(TelegramMessages.DeliveryFailed);
        failure.ButtonLabels.Should().Contain(TelegramMessages.ButtonRetry);
        failure.ButtonLabels.Should().Contain(TelegramMessages.ButtonCreateLink);
    }

    /// <summary>
    /// A file that arrives by bot lands in the sender's folder, not loose in the workspace's.
    ///
    /// <para>The drainer runs with no request, no cookie and no principal, so the tenant on the
    /// outbox row was for a long time the only identity it had — and an inbound upload was begun
    /// with <c>ownerUserId: null</c>. The same person's panel uploads went to their own folder and
    /// their Telegram ones did not, which is half of the per-user separation P2 was asked for,
    /// missing on the one path that could not see who it was serving.</para>
    /// </summary>
    [Fact]
    public async Task A_file_sent_by_bot_is_owned_by_the_person_who_sent_it()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();
        harness.SeedAccount();

        var tenant = harness.SeedTenant();
        var sender = Guid.NewGuid();
        var content = new byte[2048];
        var fileId = harness.Telegram.SeedIncomingFile(content);

        await harness.Outbox().EnqueueAsync(
            tenant.Id,
            sender,
            5001,
            TelegramOutboxKind.ReceiveDocument,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload
            {
                TelegramFileId = fileId,
                FileName = "notes.pdf",
                MimeType = "application/pdf",
            }),
            content.LongLength,
            null,
            CancellationToken.None);

        var processor = harness.Processor();
        await processor.ExecuteAsync(
            (await processor.ClaimNextAsync(true, CancellationToken.None))!,
            CancellationToken.None);

        var stored = await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.TenantId == tenant.Id);

        // The whole of it: the row the upload wrote names the person, which is what IDriveFolders
        // resolves into their own folder under the workspace's.
        stored.OwnerUserId.Should().Be(sender);
    }

    private static async Task QueueInboundAsync(
        TelegramTestHarness harness,
        Guid tenantId,
        string telegramFileId,
        long declaredSize)
    {
        await harness.Outbox().EnqueueAsync(
            tenantId,

            // Null here, because these are the tests about queueing and refusals rather than about
            // ownership — and null is what a row queued before the column existed carries.
            senderUserId: null,
            5001,
            TelegramOutboxKind.ReceiveDocument,
            null,
            JsonSerializer.Serialize(new TelegramOutboxPayload
            {
                TelegramFileId = telegramFileId,
                FileName = "notes.pdf",
                MimeType = "application/pdf",
            }),
            declaredSize,
            null,
            CancellationToken.None);
    }
}
