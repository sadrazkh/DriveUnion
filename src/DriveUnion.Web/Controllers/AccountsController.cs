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
    IGoogleOAuthCredentials credentials,
    ILogger<AccountsController> logger) : Controller
{
    private const string StateCookie = "du_google_oauth_state";

    /// <summary>
    /// This controller's own callback path, spelled once.
    ///
    /// It is a constant rather than an <c>Url.Action</c> call because the operator has to paste it
    /// into Google Cloud before anything works, so it is rendered on a screen that must come up even
    /// when nothing else about Google is configured — and behind a proxy the scheme and host it is
    /// built from come from the forwarded headers, which is the address the operator actually types.
    /// The accounts test lane fetches this path and expects something other than a 404, which is
    /// what keeps the constant honest against the routes declared below.
    /// </summary>
    private const string CallbackPath = "/accounts/callback";

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
            Google() is not null,
            GoogleSetupViewModel.From(credentials.Describe(), SuggestedRedirectUri())));
    }

    /// <summary>
    /// The OAuth client, typed in rather than deployed.
    ///
    /// The owner has no terminal on this box and nothing to hand over, so <c>user-secrets</c> and an
    /// environment variable were never going to be how this gets configured. Google still will not
    /// take a request without a client id — that part is Google's — but needing a shell to supply
    /// one was ours.
    ///
    /// A POST behind the same antiforgery token as everything else on this screen, and behind the
    /// same operator policy the controller carries: this writes the credential that reaches the
    /// operator's entire Drive pool.
    /// </summary>
    [HttpPost("google-credentials")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveGoogleCredentials([FromForm] GoogleCredentialsForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var clientId = form.ClientId?.Trim() ?? string.Empty;
        var redirectUri = form.RedirectUri?.Trim() ?? string.Empty;

        // Not trimmed away entirely: a secret is opaque and its edges are not ours to judge. But a
        // value pasted out of Google Cloud arrives with a trailing newline often enough that
        // trimming is right, and Google's secrets have never contained leading or trailing space.
        var clientSecret = form.ClientSecret?.Trim();

        var state = credentials.Describe();
        var secretAlreadyStored = state.Stored is { HasClientSecret: true };

        if (Validate(clientId, clientSecret, redirectUri, secretAlreadyStored) is { } complaint)
        {
            TempData["Error"] = complaint;
            return RedirectToAction(nameof(Index));
        }

        try
        {
            credentials.Save(clientId, clientSecret, redirectUri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The message is not shown: it carries the path of the store, and the operator cannot
            // act on it from a browser anyway.
            logger.LogError(exception, "Saving the Google OAuth client failed");
            TempData["Error"] = "ذخیره‌ی اطلاعات گوگل ناموفق بود. لاگ سرور را ببینید.";
            return RedirectToAction(nameof(Index));
        }

        // Said out loud rather than left to the badge on the field: an operator who has just typed a
        // client id and is about to wonder why Google sees a different one deserves the sentence,
        // not a colour.
        TempData["Notice"] = credentials.Describe().ConfigurationOutranksPanel
            ? "اطلاعات ذخیره شد، اما پیکربندی سرور اولویت دارد و همان اعمال می‌شود."
            : "اطلاعات OAuth گوگل ذخیره شد. حالا می‌توانید اکانت را متصل کنید.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("google-credentials/clear")]
    [ValidateAntiForgeryToken]
    public IActionResult ClearGoogleCredentials()
    {
        try
        {
            var removed = credentials.Clear();

            TempData[removed ? "Notice" : "Error"] = removed
                ? "اطلاعات OAuth ذخیره‌شده حذف شد."
                : "چیزی برای حذف وجود نداشت.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Removing the stored Google OAuth client failed");
            TempData["Error"] = "حذف اطلاعات ذخیره‌شده ناموفق بود. لاگ سرور را ببینید.";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// What the operator must register in Google Cloud, built from the address this very request
    /// arrived on.
    ///
    /// Rendered rather than described because the alternative — «https://your-domain/accounts/
    /// callback» with the domain left to the reader — is how a redirect URI ends up off by a
    /// scheme, a port or a trailing slash. Google compares the two strings and answers a mismatch
    /// with nothing anybody can debug from.
    /// </summary>
    private string SuggestedRedirectUri() =>
        $"{Request.Scheme}://{Request.Host.ToUriComponent()}{Request.PathBase}{CallbackPath}";

    /// <summary>
    /// Refuses what Google would refuse, here, where the operator can still see the form they typed
    /// it into. Every rule below is one of Google's own for an authorised redirect URI.
    /// </summary>
    private static string? Validate(
        string clientId,
        string? clientSecret,
        string redirectUri,
        bool secretAlreadyStored)
    {
        if (clientId.Length == 0) return "شناسه‌ی کلاینت (Client ID) را وارد کنید.";

        if (clientSecret is not { Length: > 0 } && !secretAlreadyStored)
        {
            return "کلید محرمانه (Client Secret) را وارد کنید.";
        }

        if (redirectUri.Length == 0) return "آدرس بازگشت (Redirect URI) را وارد کنید.";

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return "آدرس بازگشت باید یک نشانی کامل با http یا https باشد.";
        }

        // Google rejects a redirect URI with a fragment outright, and does it after the operator has
        // already left the panel and reached the consent screen.
        if (uri.Fragment.Length > 0) return "آدرس بازگشت نباید بخش # داشته باشد.";

        // http is allowed only for a loopback address; anything else must be https.
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            return "گوگل http را فقط برای localhost می‌پذیرد؛ برای بقیه‌ی آدرس‌ها https لازم است.";
        }

        return null;
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
        catch (DriveApiException exception)
        {
            // DriveApiException alone now. Unconfigured credentials used to surface here as an
            // OptionsValidationException out of the options pipeline; they arrive as
            // DriveAccountUnavailableException — which is a DriveApiException — from the token
            // service, naming the settings that are missing.
            logger.LogError(exception, "Refreshing the storage quota failed");
            TempData["Error"] = "به‌روزرسانی فضا ناموفق بود.";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The same refusal from either end of the flow. Naming the three settings is worth more than an
    /// apology here: this is the state a fresh deployment starts in, so it is the first thing an
    /// operator meets — and now the fix is on the screen they came from rather than in a shell they
    /// may not have, so the sentence says that too.
    /// </summary>
    private IActionResult Unconfigured(bool popup) => Finish(
        popup,
        succeeded: false,
        title: "پیکربندی گوگل کامل نیست",
        message: "پیکربندی OAuth گوگل کامل نیست. اطلاعات آن را در صفحه‌ی اکانت‌ها وارد کنید.",
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
    /// The OAuth client, or null when any of its three parts is missing.
    ///
    /// Resolved on every read rather than bound at startup, so a client saved on the screen below is
    /// in force for the very next request. The accounts screen has to render either way — it is the
    /// screen an operator opens to discover that nothing is configured yet, and now also the screen
    /// where they fix it.
    /// </summary>
    private GoogleOAuthOptions? Google() =>
        googleOptions.Value is { } options && options.IsConfigured() ? options : null;

    private static bool StateMatches(string? returned, string? expected)
    {
        if (string.IsNullOrEmpty(returned) || string.IsNullOrEmpty(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returned),
            Encoding.UTF8.GetBytes(expected));
    }
}
