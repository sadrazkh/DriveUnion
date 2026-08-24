using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// One outbox item, claimed and carried out. The hosted service around it is a loop; everything worth
/// arguing about is here, where a test can call it directly.
///
/// <para><b>It is sessionless.</b> There is no <c>HttpContext</c>, no cookie and no principal, and the
/// <c>TenantId</c> on the row is the only tenant identity in the operation. Everything it reads goes
/// through a tenant-scoped call with that value passed explicitly — which is the whole reason the
/// product has no global query filter, and the reason a sessionless drain has to be tested asserting a
/// <b>non-empty</b> result: the bug's entire signature is an empty one.</para>
///
/// <para><b>Three checks happen in one place, at claim time, before a single byte is read.</b> The
/// transfer slot, the free-space pre-flight and — when a plan model exists — the traffic reservation.
/// Not when the button was pressed, which may have been hours earlier, and not after the storage read,
/// which is the expensive half.</para>
/// </summary>
public sealed class TelegramOutboxProcessor(
    DriveUnionDbContext db,
    ITelegramBotGateway telegram,
    ITelegramDeliverySource source,
    ITelegramBotSettingsStore botSettings,
    IDriveClient drive,
    IUploadCoordinator uploads,
    TelegramWorkDirectory workDirectory,
    TelegramFairnessCursor fairness,
    IOptions<TelegramOptions> options,
    TimeProvider clock,
    ILogger<TelegramOutboxProcessor> logger)
{
    /// <summary>
    /// 8 MiB — thirty-two times the 256 KiB multiple the resumable protocol requires, and small
    /// enough that two concurrent transfers hold sixteen megabytes rather than sixty-four.
    /// </summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    /// <summary>
    /// A chat action lasts about five seconds, so it is repeated a little under that. Sent once at
    /// the start of a four-minute upload, the chat shows "uploading" for five seconds and then looks
    /// idle — the exact appearance of a broken bot.
    /// </summary>
    private static readonly TimeSpan ChatActionInterval = TimeSpan.FromSeconds(4);

    private readonly TelegramOptions _options = options.Value;

    /// <summary>
    /// The next item of the requested class, or null.
    ///
    /// <para><paramref name="movesBytes"/> is what keeps a text reply from queueing behind a transfer.
    /// Without the split, twenty queued deliveries saturate the uplink, the disk and every worker at
    /// once, and the chat replies that would explain what is happening are stuck behind the transfers
    /// causing it.</para>
    /// </summary>
    public async Task<TelegramOutbox?> ClaimNextAsync(bool movesBytes, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // The scheduling columns are judged here rather than in the WHERE clause. SQLite stores a
        // DateTimeOffset as text and will not compare one — the same reason the public link reader
        // and the link-token sweep both keep their date predicates out of SQL — and the queue is
        // bounded per tenant by design, so the set read back is small by construction.
        var candidates = await db.TelegramOutbox
            .AsNoTracking()
            .Where(o => o.Status == TelegramOutboxStatus.Pending)
            .Select(o => new
            {
                o.Id,
                o.TenantId,
                o.Kind,
                o.SizeBytes,
                o.NextAttemptAt,
                o.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var eligible = candidates
            .Where(o => Moves(o.Kind) == movesBytes)
            .Where(o => o.NextAttemptAt is not { } due || due <= now)
            .OrderBy(o => fairness.LastServed(o.TenantId))
            .ThenBy(o => o.CreatedAt)
            .ToList();

        foreach (var candidate in eligible)
        {
            if (movesBytes && !workDirectory.HasRoomFor(candidate.SizeBytes))
            {
                // Over the line the item stays queued and nothing is read out of storage. Beginning
                // a transfer onto a volume that cannot hold it fails at ninety-eight per cent, having
                // done all the work and filled the disk on the way out.
                logger.LogWarning(
                    "A Telegram transfer of {SizeBytes} bytes is held: the working directory volume "
                    + "cannot cover it plus headroom.",
                    candidate.SizeBytes);

                continue;
            }

            // The claim is a conditional UPDATE and its rows-affected is the arbiter. Two drainers,
            // or two loops in one, must not both carry out the same send.
            var claimed = await db.TelegramOutbox
                .Where(o => o.Id == candidate.Id && o.Status == TelegramOutboxStatus.Pending)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(o => o.Status, TelegramOutboxStatus.Claimed)
                        .SetProperty(o => o.ClaimedAt, now),
                    cancellationToken);

            if (claimed != 1) continue;

            fairness.Served(candidate.TenantId, now);

            // Read back untracked, and deliberately. Every write below this line goes through
            // ExecuteUpdateAsync, which does not touch the change tracker — so a tracked instance
            // from an earlier claim on the same context would hand the retry an Attempt of zero for
            // ever, and the budget that exists to stop a failing two-gigabyte delivery would never
            // run out.
            return await db.TelegramOutbox
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == candidate.Id, cancellationToken);
        }

        return null;
    }

    /// <summary>Claims and carries out everything currently due, and says how many items that was.</summary>
    public async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        var done = 0;

        foreach (var movesBytes in (bool[])[false, true])
        {
            while (await ClaimNextAsync(movesBytes, cancellationToken) is { } item)
            {
                await ExecuteAsync(item, cancellationToken);
                done++;

                if (cancellationToken.IsCancellationRequested) break;
            }
        }

        return done;
    }

    public async Task ExecuteAsync(TelegramOutbox item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var payload = Payload(item);

        try
        {
            var outcome = item.Kind switch
            {
                TelegramOutboxKind.SendMessage => await SendMessageAsync(item, payload, cancellationToken),
                TelegramOutboxKind.DeleteMessage => await DeleteMessageAsync(item, payload, cancellationToken),
                TelegramOutboxKind.SendDocument => await SendDocumentAsync(item, payload, cancellationToken),
                TelegramOutboxKind.ReceiveDocument => await ReceiveDocumentAsync(item, payload, cancellationToken),
                _ => TelegramCall<TelegramSentMessage>.Failed(null, $"Unknown outbox kind {item.Kind}."),
            };

            if (outcome.Ok)
            {
                await SucceedAsync(item, outcome.Value, cancellationToken);
                return;
            }

            await FailAsync(item, outcome.Failure, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unexpected exception must not leave the row Claimed for ever, which is the state
            // nothing retries and nothing reports.
            logger.LogError(ex, "A Telegram outbox item of kind {Kind} threw.", item.Kind);

            await FailAsync(item, new TelegramFailure(null, ex.Message), cancellationToken);
        }
    }

    private async Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Text is not { Length: > 0 } text)
        {
            return TelegramCall<TelegramSentMessage>.Failed(null, "A queued message carried no text.");
        }

        return await telegram.SendMessageAsync(
            new TelegramOutgoingMessage(item.ChatId, text),
            cancellationToken);
    }

    private async Task<TelegramCall<TelegramSentMessage>> DeleteMessageAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.MessageId is not { } messageId)
        {
            return TelegramCall<TelegramSentMessage>.Failed(null, "A queued deletion named no message.");
        }

        var deleted = await telegram.DeleteMessageAsync(item.ChatId, messageId, cancellationToken);

        // A message that is already gone, or older than the window a bot may delete in, is not a
        // failure worth retrying: the chat is in the state the customer asked for either way.
        return deleted.Ok || deleted.Failure.ErrorCode == 400
            ? TelegramCall<TelegramSentMessage>.Success(new TelegramSentMessage(item.ChatId, messageId))
            : TelegramCall<TelegramSentMessage>.Failed(deleted.Failure);
    }

    /// <summary>
    /// Drive → Telegram, with the cache in front of it.
    /// </summary>
    private async Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        CancellationToken cancellationToken)
    {
        if (item.StoredFileId is not { } storedFileId)
        {
            return TelegramCall<TelegramSentMessage>.Failed(null, "A queued delivery named no file.");
        }

        var ticket = await source.ResolveAsync(item.TenantId, storedFileId, cancellationToken);

        if (ticket is null)
        {
            await EditCardAsync(item, payload, TelegramMessages.FileNotAvailable, null, cancellationToken);

            // Permanent, so it does not spend two more attempts discovering the same deletion.
            return TelegramCall<TelegramSentMessage>.Failed(404, "The queued file is no longer available.");
        }

        var bot = await botSettings.ReadAsync(cancellationToken);
        var botUserId = bot.BotUserId;

        var cached = botUserId is { } id
            ? await db.TelegramFileIds
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    f => f.StoredFileId == storedFileId && f.BotUserId == id,
                    cancellationToken)
            : null;

        var send = new TelegramDocumentSend(
            item.ChatId,
            ticket.FileName,
            ticket.MimeType,
            ticket.SizeBytes,
            cached is { } hit && hit.SizeBytes == ticket.SizeBytes ? hit.FileId : null);

        TelegramCall<TelegramSentMessage> sent;

        if (send.CachedFileId is not null)
        {
            // No storage read, no bytes off this box, no working-directory copy, no egress, and no
            // download event: nothing was served through a link, so the counters that mean "somebody
            // pulled this through a link" stay honest by not counting it.
            sent = await telegram.SendDocumentAsync(send, null, cancellationToken);
        }
        else
        {
            sent = await UploadFromStorageAsync(item, ticket, send, cancellationToken);
        }

        if (!sent.Ok)
        {
            await EditCardAsync(
                item,
                payload,
                $"📄 {ticket.FileName}\n{TelegramMessages.DeliveryFailed}",
                RetryKeyboard(storedFileId),
                cancellationToken);

            return sent;
        }

        await RememberFileIdAsync(storedFileId, botUserId, ticket.SizeBytes, sent.Value, cancellationToken);
        await AfterDeliveryAsync(item, payload, ticket, sent.Value, cancellationToken);

        return sent;
    }

    private async Task<TelegramCall<TelegramSentMessage>> UploadFromStorageAsync(
        TelegramOutbox item,
        TelegramDeliveryTicket ticket,
        TelegramDocumentSend send,
        CancellationToken cancellationToken)
    {
        DriveDownload download;
        try
        {
            download = await drive.OpenDownloadAsync(
                ticket.GoogleAccountId,
                ticket.DriveFileId,
                null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is DriveApiException or DriveRateLimitedException)
        {
            // The storage read is the first and most fragile leg of a delivery, and the refusal must
            // say something true rather than the same sentence a missing file gets.
            logger.LogWarning(ex, "A Telegram delivery could not open the file for reading.");

            return TelegramCall<TelegramSentMessage>.Failed(null, TelegramMessages.TemporarilyUnavailable);
        }

        // The «uploading…» indicator, repeated for the life of the transfer, on its own task so the
        // upload is not paused to send it. It is not a message and does not spend the per-chat budget.
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = KeepAliveAsync(item.ChatId, beating.Token);

        try
        {
            // The response body is forwarded to the multipart upload unread. Nothing here buffers,
            // measures or copies it: a two-gigabyte delivery spooled anywhere is a two-gigabyte bug,
            // and it is the kind that only shows up on the one file big enough to matter.
            await using (download)
            {
                return await telegram.SendDocumentAsync(send, download.Content, cancellationToken);
            }
        }
        finally
        {
            await beating.CancelAsync();

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected: the heartbeat is stopped by cancelling it.
            }
        }
    }

    /// <summary>
    /// Telegram → Drive.
    ///
    /// <para>The bytes are spooled whether we like it or not, and not by us: against our own server
    /// <c>getFile</c> returns an absolute path, which means the server has already written the file
    /// into its working directory. There is no streaming-response-body option to prefer, because
    /// there is no response body — we open a stream over a file that already exists. What that costs
    /// is disk, on a box that has none, which is why the pre-flight ran at claim time. What it buys is
    /// a real size instead of a claimed one, and a retry that does not ask the customer to send two
    /// gigabytes again.</para>
    ///
    /// <para>And it obliges us to delete it, in a <c>finally</c>, on both outcomes — not in an
    /// <c>if (success)</c>, because the failure path is the one that leaves gigabytes behind.</para>
    /// </summary>
    private async Task<TelegramCall<TelegramSentMessage>> ReceiveDocumentAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.TelegramFileId is not { Length: > 0 } fileId)
        {
            return TelegramCall<TelegramSentMessage>.Failed(null, "A queued upload named no Telegram file.");
        }

        var handle = await telegram.GetFileAsync(fileId, cancellationToken);
        if (!handle.Ok) return TelegramCall<TelegramSentMessage>.Failed(handle.Failure);

        var name = payload.FileName is { Length: > 0 } given ? given : "file";
        var mime = payload.MimeType is { Length: > 0 } declared ? declared : "application/octet-stream";

        switch (handle.Value.Location)
        {
            case TelegramFileLocation.OnDisk onDisk:
            {
                try
                {
                    // FileInfo.Length is the truth, where the declared size was a claim. The primary
                    // check moves earlier and gets harder; the counter below stays anyway, because it
                    // costs nothing and it is the only defence on the other branch.
                    var info = new FileInfo(onDisk.Path);
                    if (!info.Exists)
                    {
                        return TelegramCall<TelegramSentMessage>.Failed(
                            null,
                            "Telegram named a local file that is not there.");
                    }

                    if (info.Length >= _options.MaxReceiveBytes)
                    {
                        return await RefuseInboundAsync(item, cancellationToken);
                    }

                    await using var body = new FileStream(
                        onDisk.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 64 * 1024,
                        useAsync: true);

                    return await StoreAsync(item, name, mime, info.Length, body, cancellationToken);
                }
                finally
                {
                    workDirectory.Delete(onDisk.Path);
                }
            }

            case TelegramFileLocation.AtUrl atUrl:
            {
                var declaredSize = handle.Value.SizeBytes ?? item.SizeBytes;

                if (declaredSize <= 0)
                {
                    return TelegramCall<TelegramSentMessage>.Failed(
                        null,
                        "Telegram declared no size for an inbound file, and a resumable upload needs one.");
                }

                if (declaredSize >= _options.MaxReceiveBytes)
                {
                    return await RefuseInboundAsync(item, cancellationToken);
                }

                var opened = await telegram.OpenRemoteFileAsync(atUrl.Url, cancellationToken);
                if (!opened.Ok) return TelegramCall<TelegramSentMessage>.Failed(opened.Failure);

                await using var body = opened.Value;

                return await StoreAsync(item, name, mime, declaredSize, body, cancellationToken);
            }

            default:
                return TelegramCall<TelegramSentMessage>.Failed(null, "Telegram gave no usable file location.");
        }
    }

    /// <summary>
    /// The ordinary upload path, fed from a stream rather than from a request body: 256 KiB-aligned
    /// ranges into a resumable session, with the last chunk the only unaligned one.
    ///
    /// <para>The byte counter is not redundant with the length check above. A declared size is a claim,
    /// and here the claim comes from a third party; a body that keeps producing bytes past what it
    /// promised is aborted rather than written.</para>
    /// </summary>
    private async Task<TelegramCall<TelegramSentMessage>> StoreAsync(
        TelegramOutbox item,
        string name,
        string mimeType,
        long sizeBytes,
        Stream body,
        CancellationToken cancellationToken)
    {
        BeginUploadResult session;
        try
        {
            // No owner, so this lands in the tenant folder rather than the sender's.
            //
            // Not a decision so much as a column that does not exist yet: TelegramOutbox carries a
            // TenantId and nothing about the person, because the tenant is all the drainer ever
            // needed. The sender *is* known at enqueue — TelegramAccount.AppUserId is what the tenant
            // was read through — so finishing this is a column on the outbox row, set by
            // TelegramOutboxWriter, and passed here. Until then a file that arrives by bot mixes into
            // the tenant folder while the same person's panel uploads go to their own, which is half
            // of the separation this phase was asked for.
            session = await uploads.BeginAsync(
                item.TenantId,
                ownerUserId: null,
                new BeginUploadRequest(name, mimeType, sizeBytes),
                cancellationToken);
        }
        catch (Exception ex) when (ex is DriveApiException or InvalidOperationException)
        {
            logger.LogWarning(ex, "A Telegram inbound upload could not be started.");

            await SayAsync(item, TelegramMessages.UploadUnavailable, cancellationToken);

            return TelegramCall<TelegramSentMessage>.Failed(null, TelegramMessages.UploadUnavailable);
        }

        var buffer = new byte[ChunkSize];
        var offset = 0L;
        UploadProgress? progress = null;

        while (offset < sizeBytes)
        {
            var want = (int)Math.Min(ChunkSize, sizeBytes - offset);
            var filled = await ReadFullyAsync(body, buffer, want, cancellationToken);

            if (filled == 0) break;

            if (offset + filled > _options.MaxReceiveBytes)
            {
                // Past the ceiling on the bytes actually seen. The session is abandoned rather than
                // completed, so nothing half-written becomes a file the customer can see.
                logger.LogWarning("A Telegram inbound file ran past the configured receive ceiling.");

                await SayAsync(
                    item,
                    TelegramMessages.InboundTooLarge(
                        TelegramFormats.Bytes(_options.MaxReceiveBytes),
                        $"{PanelBase()}/Files/Upload"),
                    cancellationToken);

                return TelegramCall<TelegramSentMessage>.Failed(413, "The inbound file exceeded the ceiling.");
            }

            using var chunk = new MemoryStream(buffer, 0, filled, writable: false);

            progress = await uploads.WriteChunkAsync(
                item.TenantId,
                session.SessionId,
                chunk,
                offset,
                filled,
                cancellationToken);

            if (progress.Status is UploadSessionStatus.Failed)
            {
                await SayAsync(item, TelegramMessages.InboundFailed, cancellationToken);

                return TelegramCall<TelegramSentMessage>.Failed(null, progress.FailureReason ?? "The upload failed.");
            }

            offset += filled;
        }

        if (progress?.StoredFileId is null)
        {
            await SayAsync(item, TelegramMessages.InboundFailed, cancellationToken);

            return TelegramCall<TelegramSentMessage>.Failed(null, "The inbound upload did not complete.");
        }

        await SayAsync(item, TelegramMessages.InboundReceived, cancellationToken);

        return TelegramCall<TelegramSentMessage>.Success(new TelegramSentMessage(item.ChatId, 0));
    }

    /// <summary>
    /// Fills the buffer to <paramref name="want"/> or to the end of the stream. A single
    /// <c>ReadAsync</c> may return fewer bytes than asked for, and a chunk shorter than 256 KiB in the
    /// middle of a resumable upload is not rejected loudly — the session simply stops acknowledging,
    /// which reads like a stalled network.
    /// </summary>
    private static async Task<int> ReadFullyAsync(
        Stream source,
        byte[] buffer,
        int want,
        CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < want)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled, want - filled), cancellationToken);
            if (read == 0) break;

            filled += read;
        }

        return filled;
    }

    private async Task<TelegramCall<TelegramSentMessage>> RefuseInboundAsync(
        TelegramOutbox item,
        CancellationToken cancellationToken)
    {
        await SayAsync(
            item,
            TelegramMessages.InboundTooLarge(
                TelegramFormats.Bytes(_options.MaxReceiveBytes),
                $"{PanelBase()}/Files/Upload"),
            cancellationToken);

        // Refused rather than failed: it is a settled answer, and retrying would fetch the same
        // oversized file twice more.
        return TelegramCall<TelegramSentMessage>.Success(new TelegramSentMessage(item.ChatId, 0));
    }

    /// <summary>
    /// What happens the moment a document lands: the card becomes the delivered state, the
    /// «دریافت کردم، پاک کن» button appears, and an armed lifetime — if there is one — becomes a
    /// queued deletion rather than an in-memory timer.
    /// </summary>
    private async Task AfterDeliveryAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        TelegramDeliveryTicket ticket,
        TelegramSentMessage sent,
        CancellationToken cancellationToken)
    {
        var ttl = _options.DeliveryMessageTtlMinutes;

        var line = ttl > 0
            ? $"📄 {ticket.FileName}\n{TelegramMessages.Delivered}\n{TelegramMessages.DeletesIn(ttl)}"
            : $"📄 {ticket.FileName}\n{TelegramMessages.Delivered}";

        await EditCardAsync(item, payload, line, AcknowledgeKeyboard(sent.MessageId), cancellationToken);

        if (ttl <= 0) return;

        // The queue is the timer. An in-memory one does not survive the restart a deploy makes
        // routine, and «پیامش پاک بشه» failing silently after a release is exactly the class of bug
        // this design keeps refusing to ship.
        db.TelegramOutbox.Add(new TelegramOutbox
        {
            Id = Guid.CreateVersion7(),
            TenantId = item.TenantId,
            ChatId = item.ChatId,
            Kind = TelegramOutboxKind.DeleteMessage,
            Payload = JsonSerializer.Serialize(new TelegramOutboxPayload { MessageId = sent.MessageId }),
            Status = TelegramOutboxStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
            NextAttemptAt = clock.GetUtcNow().AddMinutes(ttl),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static TelegramKeyboard AcknowledgeKeyboard(long messageId) =>
        TelegramKeyboard.Stacked(new TelegramInlineButton(
            TelegramMessages.ButtonAcknowledge,
            TelegramCallbackData.Encode(TelegramCallbackVerb.AcknowledgeDelivery, null, messageId)));

    private static TelegramKeyboard RetryKeyboard(Guid storedFileId) =>
        TelegramKeyboard.Grid(
        [
            new TelegramInlineButton(
                TelegramMessages.ButtonRetry,
                TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, storedFileId)),
            new TelegramInlineButton(
                TelegramMessages.ButtonCreateLink,
                TelegramCallbackData.Encode(TelegramCallbackVerb.CreateLink, storedFileId)),
        ]);

    private async Task EditCardAsync(
        TelegramOutbox item,
        TelegramOutboxPayload payload,
        string text,
        TelegramKeyboard? keyboard,
        CancellationToken cancellationToken)
    {
        if (payload.CardMessageId is { } card)
        {
            var edited = await telegram.EditMessageAsync(
                new TelegramMessageEdit(item.ChatId, card, text, keyboard),
                cancellationToken);

            if (edited.Ok) return;
        }

        await telegram.SendMessageAsync(
            new TelegramOutgoingMessage(item.ChatId, text, keyboard),
            cancellationToken);
    }

    private Task SayAsync(TelegramOutbox item, string text, CancellationToken cancellationToken) =>
        telegram.SendMessageAsync(new TelegramOutgoingMessage(item.ChatId, text), cancellationToken);

    private async Task RememberFileIdAsync(
        Guid storedFileId,
        long? botUserId,
        long sizeBytes,
        TelegramSentMessage sent,
        CancellationToken cancellationToken)
    {
        if (botUserId is not { } id) return;
        if (sent.FileId is not { Length: > 0 } fileId) return;

        var existing = await db.TelegramFileIds
            .FirstOrDefaultAsync(f => f.StoredFileId == storedFileId && f.BotUserId == id, cancellationToken);

        if (existing is null)
        {
            db.TelegramFileIds.Add(new TelegramFileId
            {
                StoredFileId = storedFileId,
                BotUserId = id,
                FileId = fileId,
                FileUniqueId = sent.FileUniqueId ?? fileId,
                SizeBytes = sizeBytes,
                CachedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            existing.FileId = fileId;
            existing.FileUniqueId = sent.FileUniqueId ?? fileId;
            existing.SizeBytes = sizeBytes;
            existing.CachedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task KeepAliveAsync(long chatId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await telegram.SendChatActionAsync(
                chatId,
                TelegramChatActions.UploadDocument,
                cancellationToken);

            await Task.Delay(ChatActionInterval, clock, cancellationToken);
        }
    }

    private async Task SucceedAsync(
        TelegramOutbox item,
        TelegramSentMessage sent,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await db.TelegramOutbox
            .Where(o => o.Id == item.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, TelegramOutboxStatus.Sent)
                    .SetProperty(o => o.SentAt, now)
                    .SetProperty(o => o.SentMessageId, sent.MessageId == 0 ? null : sent.MessageId),
                cancellationToken);
    }

    private async Task FailAsync(
        TelegramOutbox item,
        TelegramFailure failure,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // A 429 is obeyed rather than retried: the item parks until the instant Telegram named and
        // Attempt is left alone. A backlog that exhausted its retry budget on flood control would
        // fail for a reason no user could understand.
        if (failure.IsFloodControl)
        {
            var parkedUntil = now + (failure.RetryAfter ?? TimeSpan.FromSeconds(5));

            await db.TelegramOutbox
                .Where(o => o.Id == item.Id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(o => o.Status, TelegramOutboxStatus.Pending)
                        .SetProperty(o => o.NextAttemptAt, parkedUntil)
                        .SetProperty(o => o.ErrorCode, "429")
                        .SetProperty(o => o.ErrorDetail, Trim(failure.Description)),
                    cancellationToken);

            return;
        }

        if (failure.IsBotBlocked || failure.IsUserDeactivated)
        {
            await MarkUndeliverableAsync(item, failure, cancellationToken);
        }

        var attempt = item.Attempt + 1;

        // A byte-moving item costs its own size twice over on every attempt — once read out of
        // storage, once pushed to the server — so five attempts on a failing two-gigabyte delivery is
        // twenty gigabytes of reads and egress for something that has already failed four times.
        var budget = item.MovesBytes ? _options.MaxTransferAttempts : _options.MaxAttempts;

        var exhausted = attempt >= budget
                        || failure.IsPermanent
                        || failure.IsBotBlocked
                        || failure.IsUserDeactivated;

        var next = exhausted
            ? (DateTimeOffset?)null
            : now + TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5);

        await db.TelegramOutbox
            .Where(o => o.Id == item.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(
                        o => o.Status,
                        exhausted ? TelegramOutboxStatus.Failed : TelegramOutboxStatus.Pending)
                    .SetProperty(o => o.Attempt, attempt)
                    .SetProperty(o => o.NextAttemptAt, next)
                    .SetProperty(
                        o => o.ErrorCode,
                        failure.ErrorCode == null
                            ? null
                            : failure.ErrorCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetProperty(o => o.ErrorDetail, Trim(failure.Description)),
                cancellationToken);
    }

    /// <summary>
    /// The two 403s worth naming. Both stop the outbox retrying into a wall for ever and both surface
    /// on the customer's own settings card, which is the only place the fact is useful — the fix is on
    /// their phone and nowhere else.
    /// </summary>
    private async Task MarkUndeliverableAsync(
        TelegramOutbox item,
        TelegramFailure failure,
        CancellationToken cancellationToken)
    {
        var status = failure.IsUserDeactivated
            ? TelegramDeliveryStatus.Deactivated
            : TelegramDeliveryStatus.Blocked;

        var now = clock.GetUtcNow();

        await db.TelegramAccounts
            .Where(a => a.ChatId == item.ChatId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(a => a.DeliveryStatus, status)
                    .SetProperty(a => a.BlockedAt, now),
                cancellationToken);
    }

    private static bool Moves(TelegramOutboxKind kind) =>
        kind is TelegramOutboxKind.SendDocument or TelegramOutboxKind.ReceiveDocument;

    private static string Trim(string description) =>
        description.Length <= 1024 ? description : description[..1024];

    private string PanelBase() => (_options.PanelBaseUrl ?? string.Empty).TrimEnd('/');

    private static TelegramOutboxPayload Payload(TelegramOutbox item)
    {
        if (item.Payload is not { Length: > 0 } json) return new TelegramOutboxPayload();

        try
        {
            return JsonSerializer.Deserialize<TelegramOutboxPayload>(json) ?? new TelegramOutboxPayload();
        }
        catch (JsonException)
        {
            return new TelegramOutboxPayload();
        }
    }
}
