using System.Security.Claims;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    ILogger<TelegramController> logger) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = DriveUnionPolicies.Operator)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        SetShell();

        var bot = await botSettings.ReadAsync(cancellationToken);
        var health = await operatorView.ReadAsync(cancellationToken);

        return View(TelegramOperatorPageViewModel.From(
            bot,
            health,
            TempData["Notice"] as string,
            TempData["Error"] as string));
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
            return View(nameof(Link), TelegramLinkPageViewModel.Issued(state, deepLink));
        }

        return View(nameof(Link), TelegramLinkPageViewModel.From(state, error: start.Status switch
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
