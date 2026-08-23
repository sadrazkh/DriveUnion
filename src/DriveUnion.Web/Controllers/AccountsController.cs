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

    [HttpPost("connect")]
    [ValidateAntiForgeryToken]
    public IActionResult Connect()
    {
        if (Google() is not { } google)
        {
            TempData["Error"] = "پیکربندی OAuth گوگل کامل نیست.";
            return RedirectToAction(nameof(Index));
        }

        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

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

        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("Google consent returned an error");
            TempData["Error"] = "اتصال اکانت لغو شد.";
            return RedirectToAction(nameof(Index));
        }

        if (Google() is not { } google)
        {
            TempData["Error"] = "پیکربندی OAuth گوگل کامل نیست.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrEmpty(code) || !StateMatches(state, expected))
        {
            logger.LogWarning("Google callback rejected: the state did not match the one this browser was sent with");
            TempData["Error"] = "بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            // The same redirect_uri the authorize request carried, because it comes from the same
            // option. Google compares the two strings and says nothing useful when they differ.
            await directory.ConnectAsync(code, google.RedirectUri, cancellationToken);
            TempData["Notice"] = "اکانت گوگل متصل شد.";
        }
        catch (DriveApiException exception)
        {
            logger.LogError(exception, "Exchanging the Google authorization code failed");
            TempData["Error"] = "تبادل کد با گوگل ناموفق بود.";
        }

        return RedirectToAction(nameof(Index));
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
