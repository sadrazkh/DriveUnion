using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The gateway when there is no bot to talk to: it delivers nothing and says so.
///
/// <para>This is not a stub standing in for missing work — it is the truthful implementation of a seam
/// whose far end is absent. It is registered with <c>TryAdd</c> <em>after</em> the real client, so it
/// only survives where a deployment has deliberately turned the transport off; where it does survive,
/// <b>every</b> attempt really does fail, and the only honest answer is a failure. A version that
/// pretended to succeed would make the panel say a farewell was delivered to a chat that never heard
/// it.</para>
/// </summary>
public sealed class UnconfiguredTelegramBotGateway(ILogger<UnconfiguredTelegramBotGateway> logger)
    : ITelegramBotGateway
{
    private const string Reason = "No Telegram bot transport is configured in this deployment.";

    public Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        // Neither the chat id nor the text. The chat id names a person and the text can carry a
        // confirmation code, and a log line is exactly where both end up being read by somebody who
        // should not have them.
        logger.LogInformation("A Telegram message was not delivered: {Reason}", Reason);

        return Failed<TelegramSentMessage>();
    }

    public Task<TelegramCall<TelegramSentMessage>> EditMessageAsync(
        TelegramMessageEdit edit,
        CancellationToken cancellationToken) => Failed<TelegramSentMessage>();

    public Task<TelegramCall<TelegramAck>> DeleteMessageAsync(
        long chatId,
        long messageId,
        CancellationToken cancellationToken) => Failed<TelegramAck>();

    public Task<TelegramCall<TelegramAck>> SendChatActionAsync(
        long chatId,
        string action,
        CancellationToken cancellationToken) => Failed<TelegramAck>();

    public Task<TelegramCall<TelegramAck>> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken) => Failed<TelegramAck>();

    public Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramDocumentSend send,
        Stream? content,
        CancellationToken cancellationToken) => Failed<TelegramSentMessage>();

    public Task<TelegramCall<TelegramFileHandle>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken) => Failed<TelegramFileHandle>();

    public Task<TelegramCall<Stream>> OpenRemoteFileAsync(Uri url, CancellationToken cancellationToken) =>
        Failed<Stream>();

    public Task<TelegramCall<TelegramBotProfile>> GetMeAsync(CancellationToken cancellationToken) =>
        Failed<TelegramBotProfile>();

    public Task<TelegramCall<TelegramAck>> SetWebhookAsync(
        string url,
        string secretToken,
        int maxConnections,
        CancellationToken cancellationToken) => Failed<TelegramAck>();

    public Task<TelegramCall<TelegramAck>> DeleteWebhookAsync(CancellationToken cancellationToken) =>
        Failed<TelegramAck>();

    public Task<TelegramCall<TelegramWebhookInfo>> GetWebhookInfoAsync(CancellationToken cancellationToken) =>
        Failed<TelegramWebhookInfo>();

    public Task<TelegramCall<IReadOnlyList<TelegramUpdate>>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken) => Failed<IReadOnlyList<TelegramUpdate>>();

    public Task<TelegramCall<TelegramAck>> SetMyCommandsAsync(
        IReadOnlyList<TelegramBotCommand> commands,
        CancellationToken cancellationToken) => Failed<TelegramAck>();

    private static Task<TelegramCall<T>> Failed<T>() where T : class =>
        Task.FromResult(TelegramCall<T>.Failed(null, Reason));
}
