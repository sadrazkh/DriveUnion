using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Web.Infrastructure;

namespace DriveUnion.Web.Models;

/// <summary>
/// What the operator types to give the panel a bot.
///
/// <see cref="BotToken"/> is nullable and blank means «unchanged», which is the only shape a form can
/// have when the value it edits can be written but never read back.
/// </summary>
public sealed class TelegramBotForm
{
    public string? BotToken { get; set; }

    public string? BotUsername { get; set; }
}

/// <summary>
/// «ربات تلگرام» — the operator's screen.
///
/// <b>Nothing on it names a customer.</b> There is no chat id, no Telegram @username belonging to a
/// customer, no filename and no list of who has linked what: <see cref="LinkedCountText"/> and
/// <see cref="PendingCountText"/> are the whole of what this screen learns about the people using the
/// bot, and the read model behind them cannot express anything more.
/// </summary>
public sealed record TelegramOperatorPageViewModel(
    bool HasToken,
    string? BotUsername,
    long? BotUserId,
    string? UpdatedText,
    string LinkedCountText,
    string PendingCountText,
    string OutboxDepthText,
    string UpdatesLastDayText,
    string SendsFailedLastDayText,
    bool HasWebhook,
    string? WebhookRegisteredText,
    string UpdateModeText,
    TelegramWebhookHealthViewModel? Webhook,
    TelegramServerHealthViewModel Server,
    string? Notice,
    string? Error)
{
    /// <summary>True when a customer could actually start linking — a token and a @username.</summary>
    public bool IsComplete => HasToken && !string.IsNullOrEmpty(BotUsername);

    public static TelegramOperatorPageViewModel From(
        StoredTelegramBot bot,
        TelegramOperatorHealth health,
        TelegramServerHealth server,
        TelegramWebhookInfo? webhook,
        TelegramOptions options,
        string? notice,
        string? error)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return new TelegramOperatorPageViewModel(
            bot.HasToken,
            bot.BotUsername,
            bot.BotUserId,
            bot.UpdatedAt is { } updated ? DisplayFormats.PersianDateTime(updated) : null,
            PersianDigits.Count(health.LinkedAccounts),
            PersianDigits.Count(health.PendingRequests),
            PersianDigits.Count(health.OutboxDepth),
            PersianDigits.Count(health.UpdatesLastDay),
            PersianDigits.Count(health.SendsFailedLastDay),
            bot.HasWebhook,
            bot.WebhookRegisteredAt is { } registered
                ? DisplayFormats.PersianDateTime(registered)
                : null,
            options.UpdateSource is TelegramUpdateSource.Webhook ? "وبهوک" : "دریافت دوره‌ای",
            webhook is null ? null : TelegramWebhookHealthViewModel.From(webhook),
            TelegramServerHealthViewModel.From(server),
            notice,
            error);
    }
}

/// <summary>
/// Telegram's own answer about the registration.
///
/// <para><see cref="LastErrorMessage"/> is rendered <b>verbatim</b>, and that is the single most
/// useful thing on the page: it is Telegram saying why it could not reach us, in its own words, and
/// paraphrasing it throws away the only diagnosis available.</para>
///
/// <para><see cref="IsBacklogged"/> is an alarm rather than a statistic. A rising pending count is
/// what a broken webhook looks like from the outside while everything on this box appears perfectly
/// healthy.</para>
/// </summary>
public sealed record TelegramWebhookHealthViewModel(
    bool IsRegistered,
    string PendingUpdateCountText,
    bool IsBacklogged,
    string? LastErrorMessage,
    string? LastErrorText,
    string? IpAddress,
    string? MaxConnectionsText)
{
    /// <summary>Enough queued updates that something is wrong rather than merely busy.</summary>
    private const int BacklogAlarm = 20;

    public static TelegramWebhookHealthViewModel From(TelegramWebhookInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new TelegramWebhookHealthViewModel(
            !string.IsNullOrEmpty(info.Url),
            PersianDigits.Count(info.PendingUpdateCount),
            info.PendingUpdateCount >= BacklogAlarm,
            info.LastErrorMessage,
            info.LastErrorDate is { } when ? DisplayFormats.PersianDateTime(when) : null,
            info.IpAddress,
            info.MaxConnections is { } max ? PersianDigits.Count(max) : null);
    }

    // The registered URL is deliberately absent from this record. It carries the unguessable path
    // segment, which is a stored secret; a screen that could print it is a screen that eventually
    // will, into a bug-report screenshot.
}

/// <summary>
/// The Bot API server, which nothing else in the product can see.
///
/// <para><see cref="IsHoarding"/> is the second alarm and the one that ends the feature: a working
/// directory whose size stays above zero across several minutes is what a stopped delete-on-success
/// looks like while everything about the bot appears perfectly healthy. The good state is zero, which
/// is why a sweep count is not what is rendered here.</para>
/// </summary>
public sealed record TelegramServerHealthViewModel(
    string ApiBaseUrl,
    string ModeText,
    bool HasWorkDirectory,
    string WorkDirectoryBytesText,
    string WorkDirectoryFilesText,
    string? OldestFileAgeText,
    string? FreeBytesText,
    bool IsHoarding)
{
    public static TelegramServerHealthViewModel From(TelegramServerHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new TelegramServerHealthViewModel(
            health.ApiBaseUrl,
            health.LocalBotServer ? "سرور اختصاصی" : "سرویس ابری تلگرام",
            health.WorkDirectory is { Length: > 0 },
            DisplayFormats.Bytes(health.WorkDirectoryBytes),
            PersianDigits.Count(health.WorkDirectoryFiles),
            health.OldestFileAge is { } age
                ? PersianDigits.Translate($"{(int)age.TotalMinutes} دقیقه")
                : null,
            health.FreeBytes is { } free ? DisplayFormats.Bytes(free) : null,
            health.WorkDirectoryBytes > 0);

        // The path itself is not rendered. It names the Bot API working directory and there is no
        // reason for a browser, a screenshot or a log to learn where that is.
    }
}

/// <summary>
/// «اتصال تلگرام» — the customer's card.
///
/// <see cref="DeepLink"/> and <see cref="QrSvg"/> are set on exactly one response: the one that
/// answers the button press. The panel stored only the token's hash, so there is nothing to render
/// them from on any later request, and the card offers a fresh request instead of the old link.
/// </summary>
public sealed record TelegramLinkPageViewModel(
    bool BotConfigured,
    TelegramLinkedAccountViewModel? Linked,
    bool HasPendingRequest,
    bool CodeIssued,
    string? AttemptsLeftText,
    string? DeepLink,
    string? QrSvg,
    string SendCeilingText,
    string ReceiveCeilingText,
    string? Notice,
    string? Error)
{
    /// <summary>
    /// The QR code for a deep link, as inline SVG.
    ///
    /// Inline rather than an <c>&lt;img&gt;</c> pointing at an endpoint, because an endpoint that
    /// renders a QR code from a token in its URL is a token in a URL — in an access log, in a
    /// referrer, and in the browser's history. Rendered from the string the response already
    /// contains, it exists only for as long as the page does.
    /// </summary>
    public static TelegramLinkPageViewModel Issued(
        TelegramLinkState state,
        TelegramOptions options,
        string deepLink,
        string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        return new TelegramLinkPageViewModel(
            state.BotConfigured,
            null,
            true,
            state.Pending?.CodeIssued ?? false,
            AttemptsText(state.Pending?.AttemptsLeft),
            deepLink,
            QrCode.ToSvg(deepLink, "کد QR برای باز کردن ربات تلگرام"),
            TelegramFormats.Bytes(options.MaxSendBytes),
            TelegramFormats.Bytes(options.MaxReceiveBytes),
            notice,
            null);
    }

    public static TelegramLinkPageViewModel From(
        TelegramLinkState state,
        TelegramOptions options,
        string? notice = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        return new TelegramLinkPageViewModel(
            state.BotConfigured,
            state.Linked is null ? null : TelegramLinkedAccountViewModel.From(state.Linked),
            state.Pending is not null,
            state.Pending?.CodeIssued ?? false,
            AttemptsText(state.Pending?.AttemptsLeft),
            null,
            null,

            // Read from configuration rather than typed into the copy, because the two numbers are
            // different in development and in production on purpose — and a card that stated the
            // wrong one would be the product's own promise about what it can carry.
            TelegramFormats.Bytes(options.MaxSendBytes),
            TelegramFormats.Bytes(options.MaxReceiveBytes),
            notice,
            error);
    }

    private static string? AttemptsText(int? attemptsLeft) =>
        attemptsLeft is { } left && left < TelegramLinkToken.MaxAttempts
            ? PersianDigits.Plain(left)
            : null;
}

/// <summary>
/// The bound account, as the customer sees it.
///
/// <b>Never the numeric Telegram id.</b> It is an identifier the customer has no use for and support
/// does not need on a screen, and the record it is built from does not carry one.
/// </summary>
public sealed record TelegramLinkedAccountViewModel(
    string Title,
    string? Handle,
    string LinkedText,
    bool IsDeliverable,
    string StatusText)
{
    public static TelegramLinkedAccountViewModel From(TelegramLinkedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var deliverable = account.DeliveryStatus is TelegramDeliveryStatus.Active;

        return new TelegramLinkedAccountViewModel(
            account.DisplayName is { Length: > 0 } name ? name : "حساب تلگرام",
            account.Username is { Length: > 0 } handle ? $"@{handle}" : null,
            DisplayFormats.PersianDate(account.LinkedAt),
            deliverable,
            account.DeliveryStatus switch
            {
                TelegramDeliveryStatus.Active => "متصل",
                TelegramDeliveryStatus.Blocked => "مسدود شده در تلگرام",
                _ => "حساب تلگرام غیرفعال است",
            });
    }
}
