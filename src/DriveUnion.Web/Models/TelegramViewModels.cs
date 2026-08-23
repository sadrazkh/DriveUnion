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
    string? Notice,
    string? Error)
{
    /// <summary>True when a customer could actually start linking — a token and a @username.</summary>
    public bool IsComplete => HasToken && !string.IsNullOrEmpty(BotUsername);

    public static TelegramOperatorPageViewModel From(
        StoredTelegramBot bot,
        TelegramOperatorHealth health,
        string? notice,
        string? error)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(health);

        return new TelegramOperatorPageViewModel(
            bot.HasToken,
            bot.BotUsername,
            bot.BotUserId,
            bot.UpdatedAt is { } updated ? DisplayFormats.PersianDateTime(updated) : null,
            PersianDigits.Count(health.LinkedAccounts),
            PersianDigits.Count(health.PendingRequests),
            notice,
            error);
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
        string deepLink,
        string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new TelegramLinkPageViewModel(
            state.BotConfigured,
            null,
            true,
            state.Pending?.CodeIssued ?? false,
            AttemptsText(state.Pending?.AttemptsLeft),
            deepLink,
            QrCode.ToSvg(deepLink, "کد QR برای باز کردن ربات تلگرام"),
            notice,
            null);
    }

    public static TelegramLinkPageViewModel From(
        TelegramLinkState state,
        string? notice = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new TelegramLinkPageViewModel(
            state.BotConfigured,
            state.Linked is null ? null : TelegramLinkedAccountViewModel.From(state.Linked),
            state.Pending is not null,
            state.Pending?.CodeIssued ?? false,
            AttemptsText(state.Pending?.AttemptsLeft),
            null,
            null,
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
