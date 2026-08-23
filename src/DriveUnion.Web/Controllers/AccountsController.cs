using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «اکانت‌های گوگل» — the operator's pool, and the only place a Google consent screen is ever seen.
///
/// Guarded by policy rather than by not linking to it: a customer who types the address gets a 403,
/// which is the difference between an access control and a hidden button. Nothing here has a tenant
/// parameter because the accounts belong to the operator, not to anybody's tenant.
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Operator)]
[Route("accounts")]
public sealed class AccountsController(
    IGoogleAccountDirectory directory,
    IOptions<GoogleOAuthOptions> googleOptions,
    ILogger<AccountsController> logger) : Controller
{
    private const string StateCookie = "du_google_oauth_state";

    /// <summary>
    /// What the state cookie's value starts with, ahead of the nonce.
    ///
    /// The "this started in a popup" flag has to survive a round trip through Google, and there were
    /// two places it could ride: a query parameter Google echoes back, or the cookie that already
    /// carries the CSRF nonce. It rides the cookie. That value is HttpOnly, scoped to /accounts, ten
    /// minutes long, and only the antiforgery-protected POST below can write it — so nothing a link
    /// can carry decides whether the callback answers with a page that talks to <c>window.opener</c>,
    /// and the flag cannot desynchronise from the nonce the way a second cookie could.
    ///
    /// Both prefixes are four characters, so the state's length says nothing about its mode.
    /// </summary>
    private const string PopupStatePrefix = "pop.";

    private const string TopLevelStatePrefix = "top.";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var accounts = await directory.ListAsync(cancellationToken);

        ViewData[ShellContext.Key] = new ShellContext
        {
            AccountSummary = $"{accounts.Count} accounts · {DisplayFormats.Bytes(accounts.Sum(a => a.QuotaTotalBytes))}",
            UserName = User.Identity?.Name,
            UserRole = "اپراتور",
        };

        return View(new AccountsPageViewModel(
            [.. accounts.Select(AccountCardViewModel.From)],
            TempData["Notice"] as string,
            TempData["Error"] as string,
            Google() is not null));
    }

    /// <summary>
    /// Starts the consent flow. Still a POST, still antiforgery-checked, whichever window it lands in.
    ///
    /// <paramref name="popup"/> comes from a hidden field that Scripts/googleConnect.ts sets to true
    /// only once <c>window.open</c> has actually handed it a window; the form is then submitted into
    /// that window by name, so this response — a redirect to Google, or the card below when Google is
    /// unconfigured — renders inside the popup. With no JavaScript, or with popups blocked, the field
    /// stays false and this is the same-tab flow it has always been.
    /// </summary>
    [HttpPost("connect")]
    [ValidateAntiForgeryToken]
    public IActionResult Connect([FromForm] bool popup)
    {
        if (Google() is not { } google) return Unconfigured(popup);

        var state = (popup ? PopupStatePrefix : TopLevelStatePrefix)
            + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,

            // Google sends the operator back with a top-level GET from another site. Strict would
            // withhold this cookie on exactly that request and every consent would look tampered with.
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/accounts",
            MaxAge = TimeSpan.FromMinutes(10),
        });

        return Redirect(GoogleOAuthUrls.BuildAuthorizationUrl(google, state));
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        string? code,
        string? state,
        string? error,
        CancellationToken cancellationToken)
    {
        var expected = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/accounts" });

        // Read out of the cookie and never out of the query — see PopupStatePrefix. A `state` the
        // caller invented is about to be refused anyway; this way it cannot even pick the response
        // shape on the way to being refused.
        var popup = expected is not null
            && expected.StartsWith(PopupStatePrefix, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("Google consent returned an error");

            return Finish(
                popup,
                succeeded: false,
                title: "اتصال لغو شد",
                message: "اتصال اکانت لغو شد.");
        }

        if (Google() is not { } google) return Unconfigured(popup);

        if (string.IsNullOrEmpty(code) || !StateMatches(state, expected))
        {
            logger.LogWarning("Google callback rejected: the state did not match the one this browser was sent with");

            return Finish(
                popup,
                succeeded: false,
                title: "بازگشت نامعتبر",
                message: "بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.");
        }

        try
        {
            // The same redirect_uri the authorize request carried, because it comes from the same
            // option. Google compares the two strings and says nothing useful when they differ.
            await directory.ConnectAsync(code, google.RedirectUri, cancellationToken);
        }
        catch (DriveApiException exception)
        {
            logger.LogError(exception, "Exchanging the Google authorization code failed");

            return Finish(
                popup,
                succeeded: false,
                title: "تبادل با گوگل ناموفق بود",
                message: "تبادل کد با گوگل ناموفق بود.");
        }

        return Finish(
            popup,
            succeeded: true,
            title: "اکانت متصل شد",
            message: "اکانت گوگل متصل شد.");
    }

    [HttpPost("{id:guid}/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken cancellationToken)
    {
        var disconnected = await directory.DisconnectAsync(id, cancellationToken);

        if (disconnected)
        {
            TempData["Notice"] = "اکانت قطع شد. فایل‌های موجود روی آن دست‌نخورده می‌مانند.";
        }
        else
        {
            TempData["Error"] = "اکانت پیدا نشد.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/refresh-quota")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await directory.RefreshQuotaAsync(id, cancellationToken);
            TempData["Notice"] = "فضای اکانت به‌روزرسانی شد.";
        }
        catch (Exception exception) when (exception is DriveApiException or OptionsValidationException)
        {
            logger.LogError(exception, "Refreshing the storage quota failed");
            TempData["Error"] = "به‌روزرسانی فضا ناموفق بود.";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The same refusal from either end of the flow. Naming the three settings is worth more than an
    /// apology here: this is the state a fresh deployment starts in, so it is the first thing an
    /// operator meets, and the fix is three configuration keys away.
    /// </summary>
    private IActionResult Unconfigured(bool popup) => Finish(
        popup,
        succeeded: false,
        title: "پیکربندی گوگل کامل نیست",
        message: "پیکربندی OAuth گوگل کامل نیست.",
        hint: "Google:ClientId · Google:ClientSecret · Google:RedirectUri");

    /// <summary>
    /// How the consent flow ends, told once and shown twice.
    ///
    /// TempData is written in both modes because the popup's opener reloads /accounts as the flow
    /// finishes — so the page ends up saying exactly what it says without JavaScript, out of the same
    /// two slots, and there is no second copy of these sentences on the client to drift from these.
    /// The popup renders the same sentence itself, because that is where the operator is looking.
    /// </summary>
    private IActionResult Finish(
        bool popup,
        bool succeeded,
        string title,
        string message,
        string? hint = null)
    {
        TempData[succeeded ? "Notice" : "Error"] = message;

        return popup
            ? View("ConnectPopup", new ConnectPopupViewModel(succeeded, title, message, hint))
            : RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The OAuth client, or null when it is not configured.
    ///
    /// Infrastructure validates these options, so reading <c>.Value</c> is how the panel finds out
    /// they are missing. The accounts screen has to render either way — it is the screen an operator
    /// opens to discover that nothing is connected yet.
    /// </summary>
    private GoogleOAuthOptions? Google()
    {
        try
        {
            return googleOptions.Value;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }

    private static bool StateMatches(string? returned, string? expected)
    {
        if (string.IsNullOrEmpty(returned) || string.IsNullOrEmpty(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returned),
            Encoding.UTF8.GetBytes(expected));
    }
}
