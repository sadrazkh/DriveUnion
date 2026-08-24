using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// Two audiences on one route prefix: the operator's bot at <c>/telegram</c>, and the customer's
/// linking card at <c>/telegram/link</c>.
///
/// <para>The policy is on each action rather than on the controller, because the two halves are
/// authorised on opposite claims — the operator's on <c>DriveUnionPolicies.Operator</c>, the
/// customer's on <c>DriveUnionPolicies.Tenant</c>. A controller-level attribute would have to be the
/// weaker of the two, and the operator's half hands out and destroys the credential that reaches
/// every customer's bot.</para>
///
/// <para>The customer's card belongs on «تنظیمات» and will move there when that screen exists; it is
/// its own page for now rather than being bolted onto a screen this slice does not own.</para>
/// </summary>
[Route("telegram")]
public sealed class TelegramController(
    ITelegramBotSettingsStore botSettings,
    ITelegramOperatorView operatorView,
    ITelegramLinkService links,
    ITelegramBotGateway gateway,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<TelegramController> logger) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        var bot = await botSettings.ReadAsync(cancellationToken);
        var health = await operatorView.ReadAsync(cancellationToken);
        var server = operatorView.ReadServerHealth();

        // Telegram's own account of why it cannot reach us. It is only asked for when a webhook is
        // registered — against a bot with no token every page view would otherwise spend a failed
        // call to learn what the screen already knows.
        var webhook = bot.HasWebhook
            ? (await gateway.GetWebhookInfoAsync(cancellationToken)).Value
            : null;

        return View(TelegramOperatorPageViewModel.From(
            bot,
            health,
            server,
            webhook,
            telegramOptions.Value,
            TempData["Notice"] as string,
            TempData["Error"] as string));
    }

    /// <summary>
    /// «تأیید توکن» — <c>getMe</c>, which is the only proof a token works.
    ///
    /// <para>Both values it returns are stored: the @username is what every customer's deep link is
    /// built from, and the bot id is the key every cached file handle hangs off. Until this button
    /// existed the screen had both without it — the id parsed out of the token, the @username typed
    /// by the operator — which was enough to build a working deep link but was never proof the token
    /// works, and the screen said so.</para>
    /// </summary>
    [HttpPost("bot/verify")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyBot(CancellationToken cancellationToken)
    {
        var profile = await gateway.GetMeAsync(cancellationToken);

        if (!profile.Ok)
        {
            // Telegram's own words, verbatim. Paraphrasing throws away the only diagnosis available,
            // and this is an operator's screen rather than a customer's.
            TempData["Error"] = $"تلگرام توکن را نپذیرفت: {profile.Failure.Description}";

            return RedirectToAction(nameof(Index));
        }

        await botSettings.SaveVerifiedProfileAsync(
            profile.Value.BotUserId,
            profile.Value.Username,
            cancellationToken);

        // The menu Telegram draws beside the message box. It is registered here rather than at
        // startup because this is the first moment the product knows the token works — and a
        // setMyCommands against a token that does not is one more failure with nowhere to be seen.
        // Its failure is not the operator's problem: the commands work whether or not the menu lists
        // them, so it is not allowed to turn a successful verification into an error.
        await gateway.SetMyCommandsAsync(BotCommands, cancellationToken);

        TempData["Notice"] = $"توکن تأیید شد. ربات: @{profile.Value.Username}";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Five, and there is deliberately no <c>/upload</c>: uploading is "send the bot a file", which is
    /// what a person does without being told.
    /// </summary>
    private static readonly TelegramBotCommand[] BotCommands =
    [
        new("start", "شروع"),
        new("files", "فایل‌های من"),
        new("quota", "فضای مصرفی"),
        new("help", "راهنما"),
        new("unlink", "قطع اتصال"),
    ];

    /// <summary>
    /// «ثبت وبهوک» — a fresh secret, a fresh path segment and an explicit list of update kinds.
    ///
    /// <para>Both values are generated here and stored encrypted; neither is ever rendered. Rotating
    /// them on every registration is deliberate — re-registering after a leak has to be one button
    /// rather than a procedure.</para>
    /// </summary>
    [HttpPost("bot/webhook")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterWebhook(CancellationToken cancellationToken)
    {
        var baseUrl = (telegramOptions.Value.PanelBaseUrl ?? string.Empty).TrimEnd('/');

        if (baseUrl.Length == 0)
        {
            TempData["Error"] =
                "نشانی عمومی پنل تنظیم نشده است؛ بدون آن نمی‌توان وبهوک ثبت کرد.";

            return RedirectToAction(nameof(Index));
        }

        var segment = TelegramWebhookSecrets.NewValue();
        var secret = TelegramWebhookSecrets.NewValue();

        var registered = await gateway.SetWebhookAsync(
            $"{baseUrl}/telegram/{segment}",
            secret,
            telegramOptions.Value.MaxWebhookConnections,
            cancellationToken);

        if (!registered.Ok)
        {
            TempData["Error"] = $"ثبت وبهوک انجام نشد: {registered.Failure.Description}";

            return RedirectToAction(nameof(Index));
        }

        // Stored only after Telegram accepted it. The reverse order would leave this process
        // answering on a path Telegram was never told about, which is indistinguishable from a
        // working webhook until the first update fails to arrive.
        await botSettings.SaveWebhookAsync(segment, secret, cancellationToken);

        TempData["Notice"] = "وبهوک ثبت شد.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("bot/webhook/clear")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearWebhook(CancellationToken cancellationToken)
    {
        var removed = await gateway.DeleteWebhookAsync(cancellationToken);

        if (!removed.Ok)
        {
            TempData["Error"] = $"حذف وبهوک انجام نشد: {removed.Failure.Description}";

            return RedirectToAction(nameof(Index));
        }

        await botSettings.ClearWebhookAsync(cancellationToken);

        TempData["Notice"] = "وبهوک حذف شد. برای دریافت پیام‌ها باید دوباره ثبت شود.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The bot token, typed in rather than deployed — the same answer, and for the same reason, as
    /// the Google client on «اکانت‌های گوگل»: the owner has no terminal on the box.
    ///
    /// Behind the operator policy and the same antiforgery token as everything else on the screen,
    /// because this writes the credential that reaches every customer's bot.
    /// </summary>
    [HttpPost("bot")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBot(
        [FromForm] TelegramBotForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);

        var token = form.BotToken?.Trim();
        var username = form.BotUsername?.Trim().TrimStart('@');
        var stored = await botSettings.ReadAsync(cancellationToken);

        if (Validate(token, username, stored.HasToken) is { } complaint)
        {
            TempData["Error"] = complaint;
            return RedirectToAction(nameof(Index));
        }

        await botSettings.SaveAsync(token, username, CurrentUserId(), cancellationToken);

        TempData["Notice"] = "اطلاعات ربات تلگرام ذخیره شد. حالا مشتری‌ها می‌توانند حساب خود را متصل کنند.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("bot/clear")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearBot(CancellationToken cancellationToken)
    {
        var removed = await botSettings.ClearAsync(cancellationToken);

        TempData[removed ? "Notice" : "Error"] = removed
            ? "اطلاعات ربات حذف شد. اتصال‌های موجود دست‌نخورده می‌مانند اما ربات پاسخ نمی‌دهد."
            : "چیزی برای حذف وجود نداشت.";

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("link")]
    [Authorize(Policy = DriveUnionPolicies.Tenant)]
    public async Task<IActionResult> Link(CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        SetShell();

        var state = await links.DescribeAsync(userId, cancellationToken);

        return View(TelegramLinkPageViewModel.From(
            state,
            telegramOptions.Value,
            TempData["Notice"] as string,
            TempData["Error"] as string));
    }

    /// <summary>
    /// Leg one. The response it returns is the only place the deep link ever exists.
    ///
    /// It renders rather than redirecting, deliberately. A redirect would have to carry the raw
    /// token through TempData — which is a cookie in this deployment — or through the query string,
    /// and a linking token in either is the screenshot problem the second leg was built to solve,
    /// made worse by being written down somewhere the customer cannot see.
    /// </summary>
    [HttpPost("link/start")]
    [Authorize(Policy = DriveUnionPolicies.Tenant)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        SetShell();

        var start = await links.StartAsync(userId, cancellationToken);
        var state = await links.DescribeAsync(userId, cancellationToken);

        if (start is { Status: TelegramLinkStartStatus.Issued, DeepLink: { } deepLink })
        {
            return View(nameof(Link), TelegramLinkPageViewModel.Issued(state, telegramOptions.Value, deepLink));
        }

        return View(nameof(Link), TelegramLinkPageViewModel.From(state, telegramOptions.Value, error: start.Status switch
        {
            TelegramLinkStartStatus.BotNotConfigured =>
                "ربات تلگرام هنوز راه‌اندازی نشده است. با پشتیبانی تماس بگیرید.",
            TelegramLinkStartStatus.AlreadyLinked =>
                "حساب تلگرام شما از قبل متصل است.",
            _ => "این حساب امکان اتصال به تلگرام ندارد.",
        }));
    }

    /// <summary>
    /// Leg three, and the only request in the flow that writes a binding: authenticated,
    /// antiforgery-protected, and made from the settings page of the account being bound. That is
    /// what makes a forwarded deep link worth nothing on its own.
    /// </summary>
    [HttpPost("link/confirm")]
    [Authorize(Policy = DriveUnionPolicies.Tenant)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(
        [FromForm] string? code,
        CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        var outcome = await links.ConfirmAsync(userId, code, cancellationToken);

        if (outcome.Status is TelegramConfirmStatus.Linked)
        {
            TempData["Notice"] = "حساب تلگرام شما متصل شد.";
        }
        else
        {
            TempData["Error"] = outcome.Status switch
            {
                TelegramConfirmStatus.WrongCode =>
                    $"کد وارد شده درست نیست. {PersianDigits.Plain(outcome.AttemptsLeft)} تلاش باقی مانده است.",
                TelegramConfirmStatus.NotPresented =>
                    "هنوز پیامی از ربات دریافت نکرده‌اید. اول پیوند بالا را در تلگرام باز کنید.",
                TelegramConfirmStatus.AlreadyLinked =>
                    "حساب تلگرام شما از قبل متصل است.",
                TelegramConfirmStatus.TelegramAccountTaken =>
                    "این حساب تلگرام به حساب دیگری متصل شده است.",

                // TokenDead and NoPendingRequest get one sentence, because the customer's next
                // action is the same either way and the difference between "spent" and "never
                // started" is not theirs to act on.
                _ => "این درخواست معتبر نیست. یک درخواست تازه بسازید.",
            };
        }

        return RedirectToAction(nameof(Link));
    }

    /// <summary>
    /// «قطع اتصال». Deletes the identity mapping and nothing else — no file is touched, no link is
    /// revoked, and nothing the customer created goes away.
    /// </summary>
    [HttpPost("unlink")]
    [Authorize(Policy = DriveUnionPolicies.Tenant)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId) return Forbid();

        var outcome = await links.UnlinkAsync(userId, TelegramUnlinkReason.Customer, cancellationToken);

        if (!outcome.Unlinked)
        {
            TempData["Error"] = "چیزی برای قطع کردن وجود نداشت.";
            return RedirectToAction(nameof(Link));
        }

        if (outcome is { FarewellChatId: { } chatId, FarewellText: { } farewell })
        {
            // The last thing that happens, and it is sent whether or not it arrives: a chat that
            // simply stops answering is the failure this product keeps refusing to ship. Until a
            // transport exists nothing leaves the box, which the gateway reports rather than hides.
            var delivered = await gateway.TrySendMessageAsync(chatId, farewell, cancellationToken);

            if (!delivered)
            {
                logger.LogInformation("A Telegram farewell could not be delivered.");
            }
        }

        TempData["Notice"] = "اتصال حساب تلگرام قطع شد. فایل‌ها و لینک‌های شما دست‌نخورده‌اند.";

        return RedirectToAction(nameof(Link));
    }

    /// <summary>
    /// Refuses what @BotFather's own format would refuse, here, where the operator can still see the
    /// form they typed it into.
    /// </summary>
    private static string? Validate(string? token, string? username, bool tokenAlreadyStored)
    {
        if (token is not { Length: > 0 } && !tokenAlreadyStored)
        {
            return "توکن ربات را وارد کنید.";
        }

        // A token is «<شناسه عددی>:<رشته>». Checking the shape here turns "the bot never answers"
        // into "this is not a token", months earlier and on the screen where it can be corrected.
        if (token is { Length: > 0 } && TelegramLinkSecrets.BotUserIdFromToken(token) is null)
        {
            return "شکل توکن درست نیست. توکن باید مانند ۱۲۳۴۵۶۷۸۹:AA… باشد.";
        }

        if (username is not { Length: > 0 }) return "نام کاربری ربات (@username) را وارد کنید.";

        if (username.Length is < 5 or > 32) return "نام کاربری ربات باید بین ۵ تا ۳۲ نویسه باشد.";

        foreach (var c in username)
        {
            var allowed = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (!allowed) return "نام کاربری ربات فقط می‌تواند حرف انگلیسی، رقم و زیرخط داشته باشد.";
        }

        return null;
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id
            : null;

    // The pool's size and its daily quota are operator figures, and this controller serves customers
    // too; neither is set here, so the shell draws neither for anybody.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? "اپراتور" : "کاربر",
    };
}
