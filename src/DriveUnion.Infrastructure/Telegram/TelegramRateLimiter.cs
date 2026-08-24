using System.Collections.Concurrent;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The two buckets Telegram's own documentation asks for, in front of every outbound call.
///
/// <list type="bullet">
/// <item><b>One message per chat per second.</b> Verbatim from the Bots FAQ: avoid more than one
/// message per second to the same chat; short bursts may be allowed, and eventually you begin
/// receiving 429s.</item>
/// <item><b>Twenty-five per second overall</b>, against a stated bulk figure of about thirty. The
/// headroom is the same reasoning the Drive query budget uses: never learn where the ceiling is from
/// a 429.</item>
/// </list>
///
/// <para><b>A chat action is deliberately exempt.</b> The «uploading…» indicator lasts about five
/// seconds and has to be repeated for the life of a transfer that can run for minutes; counting it
/// against the per-chat message budget would mean the indicator and the message it is about cannot
/// both be sent, and the chat would look dead for the whole upload.</para>
///
/// <para>It is a singleton with a <see cref="TimeProvider"/> rather than a <c>Task.Delay</c> sprinkled
/// at call sites, for the two reasons that matter: there has to be exactly one place a call site
/// cannot route around, and the arithmetic has to be provable without waiting in real time.</para>
/// </summary>
public sealed class TelegramRateLimiter(TimeProvider clock)
{
    /// <summary>Per chat, from the documented one-message-per-second guidance.</summary>
    public static readonly TimeSpan PerChatInterval = TimeSpan.FromSeconds(1);

    /// <summary>Twenty-five a second globally: 40 ms between calls, against a stated ~30/s budget.</summary>
    public static readonly TimeSpan GlobalInterval = TimeSpan.FromMilliseconds(40);

    private readonly ConcurrentDictionary<long, DateTimeOffset> _chatReady = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _globalReady = DateTimeOffset.MinValue;

    /// <summary>
    /// How long this call has to wait. It is computed and <em>reserved</em> in one step: two callers
    /// racing for the same chat must not both be told "no wait" and then both send.
    /// </summary>
    public async Task<TimeSpan> ReserveAsync(long chatId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = clock.GetUtcNow();

            var chatReady = _chatReady.TryGetValue(chatId, out var stored) ? stored : now;
            var readyAt = chatReady > _globalReady ? chatReady : _globalReady;
            if (readyAt < now) readyAt = now;

            _chatReady[chatId] = readyAt + PerChatInterval;
            _globalReady = readyAt + GlobalInterval;

            // The dictionary is per chat and a bot with many customers accumulates one entry each.
            // They are eight bytes and a timestamp, and they are dropped once they are in the past,
            // so the map tracks active chats rather than every chat that ever existed.
            Forget(now);

            return readyAt - now;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Waits out the reservation. Separated so a test can assert the arithmetic without sleeping.</summary>
    public async Task WaitAsync(long chatId, CancellationToken cancellationToken)
    {
        var wait = await ReserveAsync(chatId, cancellationToken).ConfigureAwait(false);

        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
    }

    private void Forget(DateTimeOffset now)
    {
        if (_chatReady.Count < 1024) return;

        foreach (var entry in _chatReady)
        {
            if (entry.Value <= now) _chatReady.TryRemove(entry.Key, out _);
        }
    }
}

/// <summary>
/// The limiter, wrapped around the real gateway so that no call site can route around it.
///
/// <para>Only the calls that Telegram counts as messages go through the buckets. <c>getUpdates</c>,
/// <c>getFile</c>, <c>getMe</c>, <c>setWebhook</c> and the chat action do not: the first four are not
/// messages to a chat at all, and the fifth is exempt for the reason
/// <see cref="TelegramRateLimiter"/> gives.</para>
/// </summary>
public sealed class ThrottledTelegramBotGateway(
    ITelegramBotGateway inner,
    TelegramRateLimiter limiter) : ITelegramBotGateway
{
    public async Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await limiter.WaitAsync(message.ChatId, cancellationToken).ConfigureAwait(false);

        return await inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramCall<TelegramSentMessage>> EditMessageAsync(
        TelegramMessageEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        await limiter.WaitAsync(edit.ChatId, cancellationToken).ConfigureAwait(false);

        return await inner.EditMessageAsync(edit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramCall<TelegramAck>> DeleteMessageAsync(
        long chatId,
        long messageId,
        CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(chatId, cancellationToken).ConfigureAwait(false);

        return await inner.DeleteMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramDocumentSend send,
        Stream? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        // One call, however long the upload takes. What a long transfer consumes is a worker, a
        // connection, an uplink and a disk — not rate budget — which is why the buckets are unchanged
        // by the size of the file and the transfer slot is a separate control entirely.
        await limiter.WaitAsync(send.ChatId, cancellationToken).ConfigureAwait(false);

        return await inner.SendDocumentAsync(send, content, cancellationToken).ConfigureAwait(false);
    }

    public Task<TelegramCall<TelegramAck>> SendChatActionAsync(
        long chatId,
        string action,
        CancellationToken cancellationToken) =>
        inner.SendChatActionAsync(chatId, action, cancellationToken);

    public Task<TelegramCall<TelegramAck>> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken) =>
        inner.AnswerCallbackQueryAsync(callbackQueryId, text, cancellationToken);

    public Task<TelegramCall<TelegramFileHandle>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken) =>
        inner.GetFileAsync(fileId, cancellationToken);

    public Task<TelegramCall<Stream>> OpenRemoteFileAsync(Uri url, CancellationToken cancellationToken) =>
        inner.OpenRemoteFileAsync(url, cancellationToken);

    public Task<TelegramCall<TelegramBotProfile>> GetMeAsync(CancellationToken cancellationToken) =>
        inner.GetMeAsync(cancellationToken);

    public Task<TelegramCall<TelegramAck>> SetWebhookAsync(
        string url,
        string secretToken,
        int maxConnections,
        CancellationToken cancellationToken) =>
        inner.SetWebhookAsync(url, secretToken, maxConnections, cancellationToken);

    public Task<TelegramCall<TelegramAck>> DeleteWebhookAsync(CancellationToken cancellationToken) =>
        inner.DeleteWebhookAsync(cancellationToken);

    public Task<TelegramCall<TelegramWebhookInfo>> GetWebhookInfoAsync(CancellationToken cancellationToken) =>
        inner.GetWebhookInfoAsync(cancellationToken);

    public Task<TelegramCall<IReadOnlyList<TelegramUpdate>>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        inner.GetUpdatesAsync(offset, timeoutSeconds, cancellationToken);

    public Task<TelegramCall<TelegramAck>> SetMyCommandsAsync(
        IReadOnlyList<TelegramBotCommand> commands,
        CancellationToken cancellationToken) =>
        inner.SetMyCommandsAsync(commands, cancellationToken);
}
