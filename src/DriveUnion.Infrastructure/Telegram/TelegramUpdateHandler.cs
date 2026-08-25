using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The bot's whole surface: five commands, one card, and no dead ends.
///
/// <para><b>Every update arriving here is anonymous.</b> There is no cookie, no principal and no
/// tenant — the only identity in it is a number anyone in the world can make the bot see, simply by
/// messaging it. So the first thing that happens to every update is
/// <see cref="ITelegramIdentityReader"/>, everything downstream takes the resolved tenant as an
/// explicit argument into the same tenant-scoped services a browser request calls, and there is no
/// Telegram-specific repository with a wider scope anywhere behind this class.</para>
///
/// <para><b>Nothing here does slow work.</b> A text reply, a card and a callback acknowledgement are
/// tens of milliseconds and are sent inline; a document send or an inbound file is queued and the
/// caller returns immediately. A handler that uploaded two gigabytes before answering is a handler
/// Telegram redelivers on top of, and each redelivery would start its own multi-gigabyte
/// transfer.</para>
///
/// <para><b>The bot never names the storage provider.</b> Not in a card, not in a refusal, not in an
/// error. Every string it can send is a constant in <see cref="TelegramMessages"/>, a file name the
/// customer chose, or a number — which is what makes "nothing about the pool leaks" a test on the raw
/// outbound string rather than a habit.</para>
/// </summary>
public sealed class TelegramUpdateHandler(
    ITelegramIdentityReader identities,
    ITelegramLinkService links,
    ITelegramBotGateway telegram,
    ITelegramUpdateLedger ledger,
    ITelegramOutboxWriter outbox,
    IFileCatalog files,
    IShareLinkService shareLinks,
    TelegramWorkDirectory workDirectory,
    TelegramStrangerBudget strangers,
    IOptions<TelegramOptions> options,
    TimeProvider clock,
    ILogger<TelegramUpdateHandler> logger) : ITelegramUpdateHandler
{
    /// <summary>Ten per page, which is what fits on a phone without scrolling past the buttons.</summary>
    private const int PageSize = 10;

    private readonly TelegramOptions _options = options.Value;

    public async Task<TelegramUpdateOutcome> HandleAsync(
        TelegramUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        // The claim comes before anything else, including the identity check. Telegram redelivers an
        // update whenever the webhook answers non-2xx or times out, and a redelivery must not upload
        // the same file twice or send the same document twice — a real cost to a real customer,
        // arriving as a duplicate they did not ask for.
        if (!await ledger.TryClaimAsync(update.UpdateId, cancellationToken))
        {
            return TelegramUpdateOutcome.Duplicate;
        }

        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, cancellationToken);
        }
        else if (update.Message is { } message)
        {
            await HandleMessageAsync(message, cancellationToken);
        }

        return TelegramUpdateOutcome.Handled;
    }

    private async Task HandleMessageAsync(
        TelegramIncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (message.From is not { } sender) return;

        // Private chats only. A group's id could otherwise become bound, and then every member of
        // that group reads the tenant's files. Belt and braces: even in a private chat, from.id and
        // chat.id have to agree before anything is answered.
        if (!message.Chat.IsPrivate)
        {
            await SayAsync(message.Chat.Id, TelegramMessages.PrivateChatsOnly, null, cancellationToken);
            return;
        }

        if (message.Chat.Id != sender.Id) return;

        var identity = await identities.ResolveAsync(sender.Id, cancellationToken);

        if (identity is null)
        {
            await HandleStrangerAsync(message, sender, cancellationToken);
            return;
        }

        if (message.File is { } file)
        {
            await HandleInboundFileAsync(identity, sender, file, cancellationToken);
            return;
        }

        await HandleCommandAsync(identity, sender, message.Text?.Trim() ?? string.Empty, cancellationToken);
    }

    /// <summary>
    /// Everyone unbound gets the same string, whether they were never linked, were unlinked
    /// yesterday, or belonged to a panel user who has been removed — and the only exception is a
    /// <c>/start</c> carrying a linking token, which is the flow that turns a stranger into a
    /// customer.
    ///
    /// <para>A bot that answered "your account was disconnected" to one stranger and "unknown
    /// account" to another is an oracle for which Telegram accounts are customers of this service.
    /// The cost — a legitimate customer whose link was removed sees a generic message — is paid
    /// elsewhere: unlinking sends one farewell at the moment it happens, so the person learns why
    /// from the event rather than from the steady state.</para>
    /// </summary>
    private async Task HandleStrangerAsync(
        TelegramIncomingMessage message,
        TelegramSender sender,
        CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim() ?? string.Empty;

        if (text.StartsWith("/start", StringComparison.Ordinal))
        {
            var token = text.Length > "/start".Length ? text["/start".Length..].Trim() : null;

            var outcome = await links.PresentAsync(
                new TelegramStartRequest(
                    string.IsNullOrEmpty(token) ? null : token,
                    sender.Id,
                    message.Chat.Id,
                    sender.Username,
                    sender.DisplayName,
                    sender.LanguageCode),
                cancellationToken);

            // A stranger presenting a token is doing the thing the product asked them to do, so the
            // reply is not spent from the stranger budget. Everything else is.
            if (outcome.Status is TelegramStartStatus.CodeIssued)
            {
                await SayAsync(message.Chat.Id, outcome.ReplyText, null, cancellationToken);
                return;
            }

            if (!strangers.TryTakeReply(sender.Id, _options.StrangerRepliesPerHour)) return;

            await SayAsync(message.Chat.Id, outcome.ReplyText, null, cancellationToken);
            return;
        }

        // Past the budget the update is consumed and nothing is sent. Silence, not an error.
        if (!strangers.TryTakeReply(sender.Id, _options.StrangerRepliesPerHour)) return;

        await SayAsync(message.Chat.Id, TelegramMessages.Stranger, null, cancellationToken);
    }

    private async Task HandleCommandAsync(
        TelegramIdentity identity,
        TelegramSender sender,
        string text,
        CancellationToken cancellationToken)
    {
        // A command arrives as "/files" or "/files@TheBot" or "/files something". Only the first
        // word decides, and the @-suffix is Telegram's own, not the customer's.
        var word = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var at = word.IndexOf('@', StringComparison.Ordinal);
        if (at > 0) word = word[..at];

        switch (word)
        {
            case "/start":
                await SayAsync(sender.Id, TelegramMessages.Home, HomeKeyboard(), cancellationToken);
                return;

            case "/help":
                await SayAsync(
                    sender.Id,
                    TelegramMessages.Help(
                        TelegramFormats.Bytes(_options.MaxSendBytes),
                        TelegramFormats.Bytes(_options.MaxReceiveBytes)),
                    HomeKeyboard(),
                    cancellationToken);
                return;

            case "/files":
                await ShowFilesAsync(identity, sender.Id, 0, null, cancellationToken);
                return;

            case "/quota":
                await ShowQuotaAsync(identity, sender.Id, cancellationToken);
                return;

            case "/unlink":
                await SayAsync(
                    sender.Id,
                    TelegramMessages.UnlinkConfirm,
                    TelegramKeyboard.Stacked(
                        new TelegramInlineButton(
                            TelegramMessages.ButtonConfirmUnlink,
                            TelegramCallbackData.Encode(TelegramCallbackVerb.ConfirmUnlink)),
                        new TelegramInlineButton(
                            TelegramMessages.ButtonCancel,
                            TelegramCallbackData.Encode(TelegramCallbackVerb.Cancel))),
                    cancellationToken);
                return;

            default:
                // Anything the bot does not understand lands on the home card rather than on an
                // apology. Every message ends somewhere.
                await SayAsync(sender.Id, TelegramMessages.Home, HomeKeyboard(), cancellationToken);
                return;
        }
    }

    private async Task HandleCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        // Answered first and without exception. A button that spins for ever is the most common way
        // a bot looks broken, and every path below can end in a refusal.
        await telegram.AnswerCallbackQueryAsync(callback.Id, null, cancellationToken);

        if (callback.Chat is { IsPrivate: false }) return;

        var chatId = callback.Chat?.Id ?? callback.From.Id;
        if (chatId != callback.From.Id) return;

        var identity = await identities.ResolveAsync(callback.From.Id, cancellationToken);
        if (identity is null)
        {
            if (strangers.TryTakeReply(callback.From.Id, _options.StrangerRepliesPerHour))
            {
                await SayAsync(chatId, TelegramMessages.Stranger, null, cancellationToken);
            }

            return;
        }

        // Callback data is client-supplied and is never an authorization. A crafted callback naming
        // another tenant's file id must produce exactly what a random GUID produces, which is what
        // re-resolving every id through the tenant-scoped catalogue below is for.
        if (TelegramCallbackData.Decode(callback.Data) is not { } decoded) return;

        switch (decoded.Verb)
        {
            case TelegramCallbackVerb.ListFiles:
                await ShowFilesAsync(
                    identity,
                    chatId,
                    (int)(decoded.Number ?? 0),
                    callback.MessageId,
                    cancellationToken);
                return;

            case TelegramCallbackVerb.ShowFile when decoded.Id is { } showId:
                await ShowFileCardAsync(identity, chatId, showId, callback.MessageId, cancellationToken);
                return;

            case TelegramCallbackVerb.SendFile when decoded.Id is { } sendId:
                await QueueSendAsync(identity, chatId, sendId, callback.MessageId, cancellationToken);
                return;

            case TelegramCallbackVerb.CreateLink when decoded.Id is { } linkId:
                await CreateLinkAsync(identity, chatId, linkId, cancellationToken);
                return;

            case TelegramCallbackVerb.AcknowledgeDelivery when decoded.Number is { } messageId:
                await QueueDeleteAsync(identity, chatId, messageId, cancellationToken);
                return;

            case TelegramCallbackVerb.ConfirmUnlink:
                await UnlinkAsync(identity, chatId, cancellationToken);
                return;

            case TelegramCallbackVerb.Cancel:
                await SayAsync(chatId, TelegramMessages.Cancelled, HomeKeyboard(), cancellationToken);
                return;

            default:
                return;
        }
    }

    private async Task ShowFilesAsync(
        TelegramIdentity identity,
        long chatId,
        int offset,
        long? editMessageId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileListItem> all;
        try
        {
            all = await files.ListAsync(identity.TenantId, new FileListFilter(), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // "This cannot be fetched right now" is deliberately a different sentence from "you have
            // no files" and from the stranger reply. From a chat, three identical «خطایی رخ داد»
            // look the same, and this is precisely the moment a customer needs to know which one
            // they are looking at.
            logger.LogWarning(ex, "A Telegram file listing failed.");

            await SayAsync(chatId, TelegramMessages.TemporarilyUnavailable, HomeKeyboard(), cancellationToken);
            return;
        }

        if (all.Count == 0)
        {
            await ReplaceAsync(chatId, editMessageId, TelegramMessages.NoFiles, null, cancellationToken);
            return;
        }

        if (offset < 0) offset = 0;

        var page = all.Skip(offset).Take(PageSize).ToList();
        if (page.Count == 0)
        {
            await ReplaceAsync(chatId, editMessageId, TelegramMessages.NoFiles, HomeKeyboard(), cancellationToken);
            return;
        }

        var now = clock.GetUtcNow();
        var buttons = new List<TelegramInlineButton[]>();

        foreach (var file in page)
        {
            buttons.Add([
                new TelegramInlineButton(
                    $"{file.Name} · {TelegramFormats.Bytes(file.SizeBytes)}",
                    TelegramCallbackData.Encode(TelegramCallbackVerb.ShowFile, file.Id)),
            ]);
        }

        if (offset + page.Count < all.Count)
        {
            buttons.Add([
                new TelegramInlineButton(
                    TelegramMessages.ButtonMore,
                    TelegramCallbackData.Encode(
                        TelegramCallbackVerb.ListFiles,
                        null,
                        offset + PageSize)),
            ]);
        }

        var header = $"{TelegramFormats.Count(all.Count)} فایل · تازه‌ترین‌ها\n"
                     + $"آخرین تغییر: {TelegramFormats.Ago(page[0].ModifiedAt, now)}";

        await ReplaceAsync(
            chatId,
            editMessageId,
            header,
            TelegramKeyboard.Grid([.. buttons]),
            cancellationToken);
    }

    private async Task ShowFileCardAsync(
        TelegramIdentity identity,
        long chatId,
        Guid fileId,
        long? editMessageId,
        CancellationToken cancellationToken)
    {
        var file = await files.GetAsync(identity.TenantId, fileId, cancellationToken);

        if (file is null)
        {
            // The same card a random GUID gets. A distinguishable "not yours" is what turns an id
            // into something worth guessing.
            await ReplaceAsync(
                chatId,
                editMessageId,
                TelegramMessages.FileNotAvailable,
                HomeKeyboard(),
                cancellationToken);
            return;
        }

        var (text, keyboard) = Card(file);

        await ReplaceAsync(chatId, editMessageId, text, keyboard, cancellationToken);
    }

    /// <summary>
    /// The card, and the one decision it makes: whether «ارسال فایل» is drawn at all.
    ///
    /// <para><b>The size is decided here, when the card is rendered, and not when the button is
    /// pressed.</b> A capability the customer does not have is <em>absent</em>, not disabled — a
    /// file's size is not a condition they can fix — and at a two-gigabyte ceiling a button that
    /// cannot succeed costs two gigabytes read out of storage, minutes of a transfer slot and a
    /// failure the customer watched happen. <c>SizeBytes</c> is sitting right there.</para>
    ///
    /// <para>The link is not a consolation prize and the copy does not apologise for it: a three-
    /// gigabyte file delivered as a link is a <em>better</em> outcome than one delivered as a document,
    /// because it resumes.</para>
    /// </summary>
    private (string Text, TelegramKeyboard Keyboard) Card(FileDetail file)
    {
        var now = clock.GetUtcNow();
        var oversized = file.SizeBytes >= _options.MaxSendBytes;

        var second = oversized
            ? $"{TelegramFormats.Bytes(file.SizeBytes)} · {TelegramMessages.TooLargeToSend}"
            : $"{TelegramFormats.Bytes(file.SizeBytes)} · {TelegramFormats.Ago(file.ModifiedAt, now)}";

        var active = file.Links.Count(l => l.IsActive);

        var row = new List<TelegramInlineButton>();
        if (!oversized)
        {
            row.Add(new TelegramInlineButton(
                TelegramMessages.ButtonSend,
                TelegramCallbackData.Encode(TelegramCallbackVerb.SendFile, file.Id)));
        }

        row.Add(new TelegramInlineButton(
            TelegramMessages.ButtonCreateLink,
            TelegramCallbackData.Encode(TelegramCallbackVerb.CreateLink, file.Id)));

        // The live-link count is a fact about the file, so it goes in the card's text. Listing and
        // revoking them is a later slice — and a button labelled with a count that did something
        // else is worse than no button, because a chat gives no way to discover the difference.
        var third = active > 0
            ? $"\nلینک‌های فعال: {TelegramFormats.Digits(active)}"
            : string.Empty;

        return (
            $"📄 {file.Name}\n{second}{third}",
            TelegramKeyboard.Grid(
                [.. row],
                [new TelegramInlineButton(
                    TelegramMessages.ButtonFiles,
                    TelegramCallbackData.Encode(TelegramCallbackVerb.ListFiles, null, 0))]));
    }

    private async Task QueueSendAsync(
        TelegramIdentity identity,
        long chatId,
        Guid fileId,
        long? cardMessageId,
        CancellationToken cancellationToken)
    {
        var file = await files.GetAsync(identity.TenantId, fileId, cancellationToken);

        if (file is null)
        {
            await SayAsync(chatId, TelegramMessages.FileNotAvailable, HomeKeyboard(), cancellationToken);
            return;
        }

        // The same decision the card made, made again. The card is what should have prevented this,
        // but a callback is client-supplied and a stale card is one scroll away.
        if (file.SizeBytes >= _options.MaxSendBytes)
        {
            await SayAsync(chatId, TelegramMessages.TooLargeToSend, HomeKeyboard(), cancellationToken);
            return;
        }

        var payload = Serialize(new TelegramOutboxPayload
        {
            CardMessageId = cardMessageId,
            FileName = file.Name,
            MimeType = file.MimeType,
        });

        var queued = await outbox.EnqueueAsync(
            identity.TenantId,
            chatId,
            TelegramOutboxKind.SendDocument,
            file.Id,
            payload,
            file.SizeBytes,
            null,
            cancellationToken);

        if (queued.Status is TelegramEnqueueStatus.QueueFull)
        {
            await SayAsync(chatId, TelegramMessages.QueueFull, HomeKeyboard(), cancellationToken);
            return;
        }

        // The chat is never silent between the press and the transfer, which at this ceiling can be
        // several minutes away — and when the volume cannot hold it yet, the wait has a reason and
        // the customer is told it once, here, rather than by a card that sits at «در حال آماده‌سازی»
        // for an hour. It is deliberately not an error: the item is queued and will run.
        var waiting = !workDirectory.HasRoomFor(file.SizeBytes);

        if (cardMessageId is { } card)
        {
            var line = waiting ? TelegramMessages.DiskBusy : TelegramMessages.Preparing;

            await telegram.EditMessageAsync(
                new TelegramMessageEdit(chatId, card, $"📄 {file.Name}\n{line}"),
                cancellationToken);
        }
        else
        {
            await SayAsync(
                chatId,
                waiting ? TelegramMessages.DiskBusy : TelegramMessages.Queued,
                null,
                cancellationToken);
        }
    }

    private async Task CreateLinkAsync(
        TelegramIdentity identity,
        long chatId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await files.GetAsync(identity.TenantId, fileId, cancellationToken);

        if (file is null)
        {
            await SayAsync(chatId, TelegramMessages.FileNotAvailable, HomeKeyboard(), cancellationToken);
            return;
        }

        try
        {
            // The panel's defaults, which from a chat means no expiry and no cap: a form in a chat is
            // four messages and a state machine, and the panel already has the screen. The link is
            // still revocable from the panel, which is where a destructive action belongs.
            var link = await shareLinks.CreateAsync(
                identity.TenantId,
                new CreateShareLinkRequest(file.Id, null, null),
                cancellationToken);

            var url = $"{PanelBase()}/d/{link.Slug}";

            await SayAsync(chatId, TelegramMessages.LinkCreated(url), HomeKeyboard(), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "A Telegram share-link creation failed.");

            await SayAsync(chatId, TelegramMessages.LinkFailed, HomeKeyboard(), cancellationToken);
        }
    }

    private async Task QueueDeleteAsync(
        TelegramIdentity identity,
        long chatId,
        long messageId,
        CancellationToken cancellationToken)
    {
        // A queued row rather than a call from here, so it passes through the per-chat limiter like
        // everything else and survives the restart a deploy makes routine.
        await outbox.EnqueueAsync(
            identity.TenantId,
            chatId,
            TelegramOutboxKind.DeleteMessage,
            null,
            Serialize(new TelegramOutboxPayload { MessageId = messageId }),
            0,
            null,
            cancellationToken);
    }

    private async Task UnlinkAsync(
        TelegramIdentity identity,
        long chatId,
        CancellationToken cancellationToken)
    {
        var outcome = await links.UnlinkAsync(
            identity.AppUserId,
            TelegramUnlinkReason.Customer,
            cancellationToken);

        // The farewell is the last thing that happens, and it is what makes the uniform stranger
        // string bearable: the person learns why from the event rather than from the silence.
        if (outcome is { Unlinked: true, FarewellText: { } farewell })
        {
            await SayAsync(outcome.FarewellChatId ?? chatId, farewell, null, cancellationToken);
        }
    }

    private async Task ShowQuotaAsync(
        TelegramIdentity identity,
        long chatId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileListItem> all;
        try
        {
            all = await files.ListAsync(identity.TenantId, new FileListFilter(), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "A Telegram quota read failed.");

            await SayAsync(chatId, TelegramMessages.TemporarilyUnavailable, HomeKeyboard(), cancellationToken);
            return;
        }

        // Bytes and a count. A tenant's cap is a later slice's column, and inventing a number to
        // compare against would be worse than saying what is actually known.
        await SayAsync(
            chatId,
            TelegramMessages.Quota(
                TelegramFormats.Bytes(all.Sum(f => f.SizeBytes)),
                TelegramFormats.Digits(all.Count)),
            HomeKeyboard(),
            cancellationToken);
    }

    /// <summary>
    /// A file the customer sent the bot.
    ///
    /// <para><b>The decision is made from the declared size, before a single byte is fetched.</b> That
    /// matters twice over here: the fetch is what materialises the bytes on a box with no room, and
    /// the size is a claim from a third party. So the declared size gates the queue and a byte counter
    /// on the copy enforces the ceiling again — the drainer's job, because that is where the bytes
    /// are.</para>
    /// </summary>
    private async Task HandleInboundFileAsync(
        TelegramIdentity identity,
        TelegramSender sender,
        TelegramIncomingFile file,
        CancellationToken cancellationToken)
    {
        var declared = file.FileSize ?? 0;

        if (file.FileSize is { } size && size >= _options.MaxReceiveBytes)
        {
            await SayAsync(
                sender.Id,
                TelegramMessages.InboundTooLarge(
                    TelegramFormats.Bytes(_options.MaxReceiveBytes),
                    $"{PanelBase()}/Files/Upload"),
                HomeKeyboard(),
                cancellationToken);
            return;
        }

        var name = file.FileName is { Length: > 0 } given ? given : "file";

        var queued = await outbox.EnqueueAsync(
            identity.TenantId,
            sender.Id,
            TelegramOutboxKind.ReceiveDocument,
            null,
            Serialize(new TelegramOutboxPayload
            {
                TelegramFileId = file.FileId,
                FileName = name,
                MimeType = file.MimeType,
            }),
            declared,
            null,
            cancellationToken);

        await SayAsync(
            sender.Id,
            queued.Status switch
            {
                TelegramEnqueueStatus.QueueFull => TelegramMessages.QueueFull,

                // Accepted and held: the fetch is what materialises the bytes on this box, and the
                // pre-flight refuses to start one the volume cannot hold. Saying so once beats a
                // silence the customer would read as the file having been lost.
                _ when !workDirectory.HasRoomFor(declared) => TelegramMessages.DiskBusy,

                _ => TelegramMessages.InboundAccepted(name),
            },
            null,
            cancellationToken);
    }

    /// <summary>Every message that is not the home card offers a way back to the file list.</summary>
    private static TelegramKeyboard HomeKeyboard() =>
        TelegramKeyboard.Stacked(new TelegramInlineButton(
            TelegramMessages.ButtonFiles,
            TelegramCallbackData.Encode(TelegramCallbackVerb.ListFiles, null, 0)));

    private Task SayAsync(
        long chatId,
        string text,
        TelegramKeyboard? keyboard,
        CancellationToken cancellationToken) =>
        telegram.SendMessageAsync(new TelegramOutgoingMessage(chatId, text, keyboard), cancellationToken);

    /// <summary>
    /// Edits the message the action started from when there is one, and sends a new one otherwise.
    ///
    /// A slow action editing in place is what keeps a chat from filling with progress, and it is also
    /// why there is no percentage anywhere: an edit is a message operation, the per-chat budget is one
    /// a second, and a progress bar that respected that would spend sixty slots to say what two edits
    /// already say.
    /// </summary>
    private async Task ReplaceAsync(
        long chatId,
        long? messageId,
        string text,
        TelegramKeyboard? keyboard,
        CancellationToken cancellationToken)
    {
        if (messageId is { } id)
        {
            var edited = await telegram.EditMessageAsync(
                new TelegramMessageEdit(chatId, id, text, keyboard),
                cancellationToken);

            // An edit fails when the message is too old or identical. Falling through to a fresh
            // message is the difference between a button that does nothing and one that answers.
            if (edited.Ok) return;
        }

        await SayAsync(chatId, text, keyboard, cancellationToken);
    }

    private string PanelBase() => (_options.PanelBaseUrl ?? string.Empty).TrimEnd('/');

    private static string Serialize(TelegramOutboxPayload payload) => JsonSerializer.Serialize(payload);
}
