using DriveUnion.Core.Application;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The gateway until there is a transport: it delivers nothing and says so.
///
/// <para>This is not a stub standing in for missing work — it is the truthful implementation of a
/// seam whose far end does not exist yet. Nothing in this product can reach Telegram: there is no
/// Bot API client, no webhook and no poller, so <b>every</b> attempt to send a message really does
/// fail, and the only honest thing to return is false. A version that pretended to succeed would
/// make the panel say a farewell was delivered to a chat that never heard it.</para>
///
/// <para>The transport slice registers its own <see cref="ITelegramBotGateway"/> before calling
/// <c>AddDriveUnionTelegram</c>, and this one steps aside — the registration is <c>TryAdd</c>.</para>
/// </summary>
public sealed class UnconfiguredTelegramBotGateway(ILogger<UnconfiguredTelegramBotGateway> logger)
    : ITelegramBotGateway
{
    public Task<bool> TrySendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        // Neither the chat id nor the text. The chat id names a person and the text can carry a
        // confirmation code, and a log line is exactly the place both of those end up being read by
        // somebody who should not have them.
        logger.LogInformation(
            "A Telegram message was not delivered: no bot transport is configured in this build.");

        return Task.FromResult(false);
    }
}
