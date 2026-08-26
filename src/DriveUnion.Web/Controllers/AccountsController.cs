using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

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
    IGoogleOAuthCredentials credentials,
    IGoogleClientUsageReader usage,
    IAccountMigrations migrations,
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
        var clients = await usage.ReadAsync(cancellationToken);

        ViewData[ShellContext.Key] = new ShellContext
        {
            AccountSummary = $"{accounts.Count} accounts · {DisplayFormats.Bytes(accounts.Sum(a => a.QuotaTotalBytes))}",
            UserName = User.Identity?.Name,
            UserRole = UiText.Shell.RoleOperator,
        };

        // What each account is actually holding, which the cards above have never said. An operator
        // deciding whether to retire an account needs the file count and the workspace count, and
        // neither of those is anywhere else on this screen.
        var inventory = await migrations.InventoryAsync(cancellationToken);
        var drains = await migrations.ListAsync(cancellationToken);

        var draining = drains
            .Where(m => m.Status is AccountMigrationStatus.Pending or AccountMigrationStatus.Running)
            .Select(m => m.SourceAccountId)
            .ToHashSet();

        return View(new AccountsPageViewModel(
            [.. accounts.Select(a => AccountCardViewModel.From(
                a,
                clients.Accounts.GetValueOrDefault(a.Id)))],
            TempData["Notice"] as string,
            TempData["Error"] as string,
            Google() is not null,
            GoogleSetupViewModel.From(
                credentials.Describe(),
                SuggestedRedirectUri(),
                clients.AccountsPerClientId),
            [.. inventory.Select(i => ToHolding(i, draining.Contains(i.AccountId)))],
            [.. drains.Select(ToDrainRow)]));
    }

    /// <summary>
    /// Moves everything off one account and onto another.
    ///
    /// <para>Queued rather than done: a drain is a file at a time through this server, and forty
    /// thousand of them is not something a form post waits for. The worker picks it up; this screen
    /// shows how far it has got.</para>
    /// </summary>
    [HttpPost("{id:guid}/drain")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Drain(Guid id, Guid target, CancellationToken cancellationToken)
    {
        var result = await migrations.StartAsync(id, target, cancellationToken);

        if (result.Started)
        {
            TempData["Notice"] = UiText.Accounts.DrainStarted;

            logger.LogInformation(
                "An operator started draining account {SourceAccountId} into {TargetAccountId}.",
                id,
                target);
        }
        else
        {
            // Every refusal here is something the operator can see and fix, so it is a sentence on
            // the screen rather than a status code.
            TempData["Error"] = result.Refusal switch
            {
                MigrationRefusal.SameAccount => UiText.Accounts.RefusalSameAccount,
                MigrationRefusal.TargetNotHealthy => UiText.Accounts.RefusalTargetNotHealthy,
                MigrationRefusal.TargetTooSmall => UiText.Accounts.RefusalTargetTooSmall,
                MigrationRefusal.AlreadyRunning => UiText.Accounts.RefusalAlreadyRunning,
                _ => UiText.Accounts.RefusalUnknownAccount,
            };
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("drains/{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDrain(Guid id, CancellationToken cancellationToken)
    {
        if (await migrations.CancelAsync(id, cancellationToken))
        {
            TempData["Notice"] = UiText.Accounts.DrainCancelled;
        }

        return RedirectToAction(nameof(Index));
    }

    private static AccountHoldingViewModel ToHolding(AccountInventory account, bool isDraining) =>
        new(
            account.AccountId,
            string.IsNullOrWhiteSpace(account.Label) ? account.Email : account.Label,
            account.Status switch
            {
                GoogleAccountStatus.Healthy => UiText.Accounts.StatusHealthy,
                GoogleAccountStatus.Paused => UiText.Accounts.StatusPaused,
                _ => UiText.Accounts.StatusDisconnected,
            },
            account.Status == GoogleAccountStatus.Healthy,
            Numerals.Count(account.FileCount),
            DisplayFormats.Bytes(account.LiveBytes),

            // «Unknown» and not «0 B». Nobody has asked Google yet for an account that has never had
            // its quota refreshed, and reporting that as no free space would read as an account
            // that is full.
            account.FreeBytes is { } free ? DisplayFormats.Bytes(free) : UiText.Accounts.Unknown,
            Numerals.Count(account.TenantCount),
            account.FileCount == 0,
            isDraining);

    private static MigrationRowViewModel ToDrainRow(AccountMigrationView drain) =>
        new(
            drain.Id,
            drain.SourceLabel,
            drain.TargetLabel,
            drain.Status switch
            {
                AccountMigrationStatus.Pending => UiText.Accounts.DrainQueued,
                AccountMigrationStatus.Running => UiText.Accounts.DrainMoving,
                AccountMigrationStatus.Completed => UiText.Accounts.DrainDone,
                AccountMigrationStatus.Cancelled => UiText.Accounts.DrainStopped,
                _ => UiText.Accounts.DrainBroken,
            },
            drain.Status is AccountMigrationStatus.Pending or AccountMigrationStatus.Running,
            Numerals.Count(drain.FilesMoved),
            Numerals.Count(drain.FilesFailed),
            Numerals.Count(drain.FilesRemaining),
            drain.FailureReason,
            DisplayFormats.PanelDateTime(drain.CreatedAt));

    /// <summary>
    /// The OAuth client, typed in rather than deployed.
    ///
    /// The owner has no terminal on this box and nothing to hand over, so <c>user-secrets</c> and an
    /// environment variable were never going to be how this gets configured. Google still will not
    /// take a request without a client id — that part is Google's — but needing a shell to supply
    /// one was ours.
    ///
    /// A blank <see cref="GoogleCredentialsForm.Id"/> adds a client; a filled one edits that client.
    /// The two are different forms on the screen, so an edit cannot become an accidental insert.
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

        // A secret already stored is only an excuse to leave the field blank when this is an edit of
        // the client that holds it. Adding a client always needs its own.
        var secretAlreadyStored = form.Id is { } editing
            && credentials.Describe().StoredClients.Any(c => c.Id == editing && c.HasClientSecret);

        if (Validate(clientId, clientSecret, redirectUri, secretAlreadyStored) is { } complaint)
        {
            TempData["Error"] = complaint;
            return RedirectToAction(nameof(Index));
        }

        var saved = credentials.Save(form.Id, clientId, clientSecret, redirectUri);

        if (saved.Outcome is not GoogleOAuthClientSave.Saved)
        {
            TempData["Error"] = saved.Outcome is GoogleOAuthClientSave.DuplicateClientId
                ? UiText.Accounts.ClientAlreadySaved
                : UiText.Accounts.ClientNotFound;

            return RedirectToAction(nameof(Index));
        }

        // Said out loud rather than left to the badge on the field: an operator who has just typed a
        // client id and is about to wonder why Google sees a different one deserves the sentence,
        // not a colour.
        TempData["Notice"] = credentials.Describe().ConfigurationOutranksPanel
            ? UiText.Accounts.SavedButOverridden
            : UiText.Accounts.Saved;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Promotes one stored client to the one new connections run with.
    ///
    /// This is how a second Google project becomes reachable: the accounts already in the pool keep
    /// being refreshed with the client that connected them — that binding is on the account row —
    /// and only the next consent flow uses this one.
    /// </summary>
    [HttpPost("google-credentials/{id:guid}/use")]
    [ValidateAntiForgeryToken]
    public IActionResult UseGoogleClient(Guid id)
    {
        if (!credentials.MakeDefault(id))
        {
            TempData["Error"] = UiText.Accounts.ClientNotFound;
            return RedirectToAction(nameof(Index));
        }

        // Promoting a client while configuration is supplying one changes nothing about the next
        // connection, and an operator who was not told that would go looking for the fault in Google
        // Cloud. Configuration outranks the panel; this is where that stops being abstract.
        TempData["Notice"] = credentials.Describe().ClientId.Source is GoogleCredentialSource.Configuration
            ? UiText.Accounts.ClientInUseButOverridden
            : UiText.Accounts.ClientInUse;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Forgets a stored client — unless accounts are still refreshed with it.
    ///
    /// That refusal is the whole reason this is not a plain delete. A refresh token can only be
    /// presented by the client that issued it, so removing a client in use does not fail here: it
    /// fails an hour later, on every account bound to it at once, as uploads reporting that storage
    /// is unavailable. Which is exactly how this product lost its pool once already.
    /// </summary>
    [HttpPost("google-credentials/{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveGoogleClient(Guid id)
    {
        var removal = credentials.Remove(id);

        switch (removal.Outcome)
        {
            case GoogleOAuthClientRemoval.Removed:
                TempData["Notice"] = UiText.Accounts.Cleared;
                break;

            case GoogleOAuthClientRemoval.InUseByAccounts:
                TempData["Error"] = UiText.Accounts.ClientInUseByAccounts(
                    string.Join(UiText.Accounts.LabelSeparator, removal.AccountLabels));
                break;

            default:
                TempData["Error"] = UiText.Accounts.NothingToClear;
                break;
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
        if (clientId.Length == 0) return UiText.Accounts.ClientIdRequired;

        if (clientSecret is not { Length: > 0 } && !secretAlreadyStored)
        {
            return UiText.Accounts.ClientSecretRequired;
        }

        if (redirectUri.Length == 0) return UiText.Accounts.RedirectUriRequired;

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return UiText.Accounts.RedirectUriNotAbsolute;
        }

        // Google rejects a redirect URI with a fragment outright, and does it after the operator has
        // already left the panel and reached the consent screen.
        if (uri.Fragment.Length > 0) return UiText.Accounts.RedirectUriHasFragment;

        // http is allowed only for a loopback address; anything else must be https.
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            return UiText.Accounts.RedirectUriNeedsHttps;
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
    public IActionResult Connect([FromForm] bool popup) => StartConsent(popup, loginHint: null);

    /// <summary>
    /// The same consent flow, aimed at one account that is already in the pool.
    ///
    /// Separate from <see cref="Connect"/> because the two are different intentions and the screen
    /// used to spell them with one button — which is half of why adding a second account looked
    /// impossible. This one carries the account's address as a <c>login_hint</c> so Google's chooser
    /// opens on it, and the callback still stores whatever Google actually returns: reconnecting is
    /// a credential replacement keyed on the address Drive reports, not a promise about which
    /// account the operator will pick.
    /// </summary>
    [HttpPost("{id:guid}/reconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconnect(
        Guid id,
        [FromForm] bool popup,
        CancellationToken cancellationToken)
    {
        var account = (await directory.ListAsync(cancellationToken)).FirstOrDefault(a => a.Id == id);

        if (account is null)
        {
            // Through Finish rather than a redirect, because in the popup a redirect would render
            // the whole panel inside a 520px window instead of saying what went wrong.
            return Finish(
                popup,
                succeeded: false,
                title: UiText.Accounts.AccountNotFoundTitle,
                message: UiText.Accounts.AccountNotFound);
        }

        return StartConsent(popup, account.Email);
    }

    /// <summary>
    /// Sends the operator to Google with a fresh nonce in a cookie, whichever button started it.
    /// </summary>
    private IActionResult StartConsent(bool popup, string? loginHint)
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

        return Redirect(GoogleOAuthUrls.BuildAuthorizationUrl(google, state, loginHint));
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
                title: UiText.Accounts.ConnectCancelledTitle,
                message: UiText.Accounts.ConnectCancelled);
        }

        if (Google() is not { } google) return Unconfigured(popup);

        if (string.IsNullOrEmpty(code) || !StateMatches(state, expected))
        {
            logger.LogWarning("Google callback rejected: the state did not match the one this browser was sent with");

            return Finish(
                popup,
                succeeded: false,
                title: UiText.Accounts.CallbackInvalidTitle,
                message: UiText.Accounts.CallbackInvalid);
        }

        Guid connectedId;
        try
        {
            // The same redirect_uri the authorize request carried, because it comes from the same
            // option. Google compares the two strings and says nothing useful when they differ.
            connectedId = await directory.ConnectAsync(code, google.RedirectUri, cancellationToken);
        }
        catch (DriveApiException exception)
        {
            logger.LogError(exception, "Exchanging the Google authorization code failed");

            return Finish(
                popup,
                succeeded: false,
                title: UiText.Accounts.ExchangeFailedTitle,
                message: UiText.Accounts.ExchangeFailed);
        }

        // Which account, said out loud. The operator has just answered a chooser, and the whole
        // reason a second account looked impossible is that the panel used to report every outcome
        // with the same sentence — so approving the account that was already connected read exactly
        // like adding a new one. One read of a table that holds two or three rows, right after a
        // round trip to Google, is not a cost worth weighing against that.
        var connected = (await directory.ListAsync(cancellationToken))
            .FirstOrDefault(a => a.Id == connectedId);

        return Finish(
            popup,
            succeeded: true,
            title: UiText.Accounts.ConnectedTitle,
            message: connected is null
                ? UiText.Accounts.Connected
                : UiText.Accounts.ConnectedNamed(connected.Label, connected.Email));
    }

    [HttpPost("{id:guid}/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken cancellationToken)
    {
        var disconnected = await directory.DisconnectAsync(id, cancellationToken);

        if (disconnected)
        {
            TempData["Notice"] = UiText.Accounts.Disconnected;
        }
        else
        {
            TempData["Error"] = UiText.Accounts.AccountNotFound;
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
            TempData["Notice"] = UiText.Accounts.QuotaRefreshed;
        }
        catch (DriveApiException exception)
        {
            // DriveApiException alone now. Unconfigured credentials used to surface here as an
            // OptionsValidationException out of the options pipeline; they arrive as
            // DriveAccountUnavailableException — which is a DriveApiException — from the token
            // service, naming the settings that are missing.
            logger.LogError(exception, "Refreshing the storage quota failed");
            TempData["Error"] = UiText.Accounts.QuotaRefreshFailed;
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
        title: UiText.Accounts.UnconfiguredTitle,
        message: UiText.Accounts.Unconfigured,
        hint: UiText.Accounts.ConfigurationKeys);

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
        credentials.InForce is { } options && options.IsConfigured() ? options : null;

    private static bool StateMatches(string? returned, string? expected)
    {
        if (string.IsNullOrEmpty(returned) || string.IsNullOrEmpty(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returned),
            Encoding.UTF8.GetBytes(expected));
    }
}
