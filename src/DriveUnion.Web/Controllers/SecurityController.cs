using System.Text;
using System.Text.Encodings.Web;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «امنیت حساب» — where somebody puts a second lock on their own account.
///
/// <para><b>Why here and not in the Identity area.</b> The area exists so that
/// <c>/Identity/Account/Login</c> is a name the cookie handler can be pointed at, and everything in
/// it is a screen you see while you are <i>not</i> signed in — the sign-in form, the first-run setup,
/// the two refusal cards. This screen is the opposite: it is a panel page, wearing the panel's
/// shell, reached from the panel's sidebar, by somebody who is already signed in. It belongs beside
/// <c>/keys</c> and <c>/notifications</c>, which are the other two screens where a person manages
/// their own credentials. The second step <i>at</i> sign-in is a different matter and does live in
/// the area, on <c>AccountController</c>, because it is part of the sign-in flow.</para>
///
/// <para><b>Why <c>[Authorize]</c> and not the tenant policy.</b> Two-step sign-in is a fact about an
/// account, not about a workspace, and operator staff have no workspace at all — the audience that
/// most needs this screen is exactly the one the tenant policy refuses. <c>/notifications</c> is
/// behind the same bare <c>[Authorize]</c> for the same reason.</para>
///
/// <para><b>No migration.</b> Identity's own schema already carries every column this needs:
/// <c>AspNetUsers.TwoFactorEnabled</c> for the switch, and <c>AspNetUserTokens</c> for the
/// authenticator key and the recovery codes, both written through <c>UserManager</c>'s token store.
/// Nothing in this slice adds a table, a column or an index.</para>
/// </summary>
[Authorize]
[Route("security")]
public sealed class SecurityController(
    UserManager<AppUser> users,
    SignInManager<AppUser> signInManager,
    UrlEncoder urlEncoder,
    ILogger<SecurityController> logger) : Controller
{
    /// <summary>
    /// How many recovery codes a set holds.
    ///
    /// <para>Ten, which is what every product that does this ships, and the number is a trade rather
    /// than a convention: each one is a credential that bypasses the phone, so a longer list is more
    /// paper to lose, and a shorter one runs out during the week somebody is between phones. Ten
    /// prints on one line of a notebook and survives a couple of emergencies.</para>
    /// </summary>
    private const int RecoveryCodeCount = 10;

    /// <summary>
    /// What the <c>otpauth://</c> URI names this deployment as, which is the label the code app puts
    /// above the six digits.
    ///
    /// <para>A constant rather than the request's host, deliberately: a person with three accounts on
    /// three deployments needs to be able to tell which row is which, and a host name that changes
    /// when the panel moves behind a new domain silently orphans the entry already on their phone —
    /// the issuer is part of what the app keyed the secret under.</para>
    /// </summary>
    private const string Issuer = "Drive Union";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync() is not { } user) return await SessionNamesNobody();

        SetShell();

        return View(await ReadAsync(user, cancellationToken));
    }

    /// <summary>
    /// Turns it on, and only ever on the strength of a code the caller could produce.
    ///
    /// <para>The key is on screen by the time this is posted, so «they have seen the key» is a fact
    /// about the page and not about the phone. Enabling on that alone is the version of this feature
    /// that locks people out of their own account: somebody who mistyped the key into their app, or
    /// scanned nothing at all and pressed the button, would be turned away by their own second
    /// factor at the next sign-in with no way to prove anything. A correct code is the only evidence
    /// that the secret reached the device that will be asked for it.</para>
    /// </summary>
    [HttpPost("enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(string? code, CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync() is not { } user) return await SessionNamesNobody();

        if (user.TwoFactorEnabled)
        {
            // Two tabs, or a form saved from before. Not an error — the caller wanted it on and it
            // is on — but it must not fall through and mint a second set of recovery codes, which
            // would silently kill the set they are holding.
            return Done(UiText.Security.AlreadyOn);
        }

        if (await LockedOutAsync(user)) return Refused(UiText.Identity.LockedOut);

        if (Blank(code)) return Refused(UiText.Security.CodeRequired);

        if (!await VerifyAuthenticatorAsync(user, code!))
        {
            await CountAgainstLockoutAsync(user);

            return Refused(UiText.Security.BadCode);
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        await users.ResetAccessFailedCountAsync(user);

        // Generated here and not on first sight of the screen: a set of recovery codes that exists
        // before the switch does is a set somebody may have written down for a second factor that
        // never got turned on, and the next set — the real one — would look identical to it.
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        await KeepThisBrowserSignedInAsync(user);

        logger.LogInformation("Two-step sign-in was turned on for {Email}.", user.Email);

        return Done(UiText.Security.TurnedOn, codes);
    }

    /// <summary>
    /// Turns it off, and asks for a second factor to do it.
    ///
    /// <para>An open session is not enough, and that is the whole point of the action. The threat a
    /// second factor answers is somebody who has the password or the cookie; if the cookie alone
    /// could take the second lock off, the second lock is a speed bump with a switch beside it.</para>
    ///
    /// <para>A recovery code is accepted here as well as an app code, and that is not a weakening —
    /// it is the only way out of the trap. Somebody whose phone is at the bottom of a river signs in
    /// with a recovery code, and if this action took app codes only they could never turn it off,
    /// never re-enrol the new phone, and would burn one code per sign-in until the list ran out and
    /// the account was gone. Both are things they have rather than things they know, which is the
    /// question a second factor asks.</para>
    /// </summary>
    [HttpPost("disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(string? code, CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync() is not { } user) return await SessionNamesNobody();

        if (!user.TwoFactorEnabled) return Done(UiText.Security.AlreadyOff);

        if (await LockedOutAsync(user)) return Refused(UiText.Identity.LockedOut);

        if (Blank(code)) return Refused(UiText.Security.CodeRequired);

        if (!await VerifySecondFactorAsync(user, code!))
        {
            await CountAgainstLockoutAsync(user);

            return Refused(UiText.Security.BadCode);
        }

        await users.SetTwoFactorEnabledAsync(user, false);

        // The key dies with the switch. Left alive, an entry on a phone that was handed on, sold or
        // stolen would come back to life the moment somebody turned this on again — and the screen
        // that turned it on would show no sign of it, because there is no «which devices hold this
        // key» to show. Turning it on again is a fresh secret, and the screen says so.
        await users.ResetAuthenticatorKeyAsync(user);
        await users.ResetAccessFailedCountAsync(user);

        await KeepThisBrowserSignedInAsync(user);

        logger.LogInformation("Two-step sign-in was turned off for {Email}.", user.Email);

        return Done(UiText.Security.TurnedOff);
    }

    /// <summary>
    /// A new set of recovery codes, which is also how a spent set is replaced.
    ///
    /// <para>Behind a code for the same reason <see cref="Disable"/> is: minting a fresh set from an
    /// open session would hand whoever holds that session ten permanent ways back in, and kill the
    /// ten the owner is holding at the same time.</para>
    /// </summary>
    [HttpPost("recovery-codes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecoveryCodes(string? code, CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync() is not { } user) return await SessionNamesNobody();

        if (!user.TwoFactorEnabled) return Done(UiText.Security.AlreadyOff);

        if (await LockedOutAsync(user)) return Refused(UiText.Identity.LockedOut);

        if (Blank(code)) return Refused(UiText.Security.CodeRequired);

        if (!await VerifySecondFactorAsync(user, code!))
        {
            await CountAgainstLockoutAsync(user);

            return Refused(UiText.Security.BadCode);
        }

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);

        await users.ResetAccessFailedCountAsync(user);

        logger.LogInformation("A new set of recovery codes was made for {Email}.", user.Email);

        return Done(UiText.Security.Regenerated, codes);
    }

    // ------------------------------------------------------------------ reading the account

    private async Task<SecurityPageViewModel> ReadAsync(AppUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var isOn = await users.GetTwoFactorEnabledAsync(user);

        // The shared key is drawn only while the switch is off, and the reason is not tidiness. Once
        // it is on, the key is a standing second factor: a screen that goes on showing it turns every
        // borrowed laptop with a live session into a way of cloning somebody's authenticator, which
        // is the exact attack the second factor was added to stop. Off, there is nothing to protect
        // yet and the key is the instruction.
        var key = isOn ? null : await SharedKeyAsync(user);

        return new SecurityPageViewModel(
            IsOperator: User.IsOperator(),
            IsOn: isOn,
            SharedKey: key is null ? null : Grouped(key),
            AuthenticatorUri: key is null ? null : AuthenticatorUri(user, key),
            RecoveryCodesLeft: isOn ? await users.CountRecoveryCodesAsync(user) : 0,

            // TempData and never the model store, for exactly one render. It is the only moment
            // these exist outside the reader's hands — the row keeps a hash of each — and a set that
            // came back on F5 would be a page of live credentials sitting in a browser's history.
            FreshRecoveryCodes: TempData["RecoveryCodes"] as string,
            Notice: TempData["Notice"] as string,
            Refusal: TempData["Refusal"] as string);
    }

    /// <summary>
    /// The account's TOTP secret, made on first sight of the setup card and kept until it is used or
    /// thrown away.
    ///
    /// <para>Generated on a GET, which is a side effect on a read and is worth defending. It happens
    /// once — every later render finds the key already there and hands back the same one — so a
    /// refresh, a back button or a second tab cannot invalidate a key somebody has already typed
    /// into their phone, which is the failure a «make me a key» button would introduce the first
    /// time somebody pressed it twice. Nothing is enabled by it; a key with the switch off is an
    /// unused row in the token table.</para>
    ///
    /// <para>Resetting it bumps the security stamp, and this deployment validates that stamp on
    /// every request (<c>AddDriveUnionTenancy</c> sets the interval to zero) — so without the
    /// re-issue below, opening this screen would sign the reader out on their very next click.</para>
    /// </summary>
    private async Task<string> SharedKeyAsync(AppUser user)
    {
        if (await users.GetAuthenticatorKeyAsync(user) is { Length: > 0 } existing) return existing;

        await users.ResetAuthenticatorKeyAsync(user);
        await KeepThisBrowserSignedInAsync(user);

        return (await users.GetAuthenticatorKeyAsync(user))!;
    }

    /// <summary>
    /// The <c>otpauth://</c> URI a code app understands, built here and never sent anywhere.
    ///
    /// <para><b>Why there is no QR code on this screen.</b> The key must not leave the deployment, so
    /// the chart API every tutorial reaches for is out — handing a URL containing the secret to
    /// Google's servers to be drawn is the one thing a product built on «Google never learns whose
    /// bytes these are» cannot do. That left drawing it here, in process, which means a QR encoder
    /// written by hand: Reed–Solomon over GF(256), the block-interleaving tables, the BCH format
    /// word, and the eight masks with their penalty scoring. It is perhaps three hundred lines whose
    /// only honest test is a camera, and there is no camera in this repository. An encoder that is
    /// nearly right produces a picture that scans on nobody's phone, and the screen where that fails
    /// is the screen somebody opened to protect their films.</para>
    ///
    /// <para>So the key is text, in groups of four, with the three steps written out beside it —
    /// every code app on both stores takes a typed key, and the typing happens exactly once. This
    /// link is the shortcut for the case that matters most: a phone that already has a code app
    /// registers the <c>otpauth</c> scheme, so one press adds the entry with no typing at all. It
    /// carries the secret, which is why it goes no further than the page that is already showing it
    /// — a custom scheme is handed to a local application and sends no referrer anywhere.</para>
    ///
    /// <para>If the share-link QR of roadmap item A6 lands with a tested encoder, this screen gains a
    /// picture by rendering that URI beside the key. Nothing here needs to change for it, and this
    /// slice deliberately does not invent a shared one to be adopted — a duplicate that is never
    /// written is cheaper than an interface two worktrees have to agree on.</para>
    /// </summary>
    private string AuthenticatorUri(AppUser user, string key) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            urlEncoder.Encode(Issuer),
            urlEncoder.Encode(user.Email ?? user.UserName ?? user.Id.ToString()),
            key);

    /// <summary>
    /// The key in groups of four, which is how it is read aloud and how it is typed.
    ///
    /// <para>Identity's key is thirty-two base-32 characters in one run. Copied into a phone one
    /// character at a time it is a place to lose your position; the spaces are ignored by every code
    /// app and by <see cref="VerifyAuthenticatorAsync"/>, which strips them before Identity sees
    /// them.</para>
    /// </summary>
    private static string Grouped(string key)
    {
        var text = new StringBuilder(key.Length + (key.Length / 4));

        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0) text.Append(' ');

            text.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ checking a code

    /// <summary>
    /// A six-digit code from the code app, with whatever a person's fingers put around it removed.
    ///
    /// <para>Spaces because the app shows «123 456», and the dash because a keyboard on a phone
    /// offers one. Identity parses the code as an integer and refuses anything else, so a stray
    /// space would be an unexplained refusal on a code the reader can see is correct.</para>
    /// </summary>
    private async Task<bool> VerifyAuthenticatorAsync(AppUser user, string code) =>
        await users.VerifyTwoFactorTokenAsync(
            user,
            users.Options.Tokens.AuthenticatorTokenProvider,
            code.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal));

    /// <summary>
    /// The app's code, or a recovery code — see <see cref="Disable"/> for why both.
    ///
    /// <para>The app is tried first, so an ordinary code never touches the recovery list. A recovery
    /// code that matches is spent by the attempt, which is the property that makes it a recovery
    /// code rather than a second password.</para>
    /// </summary>
    private async Task<bool> VerifySecondFactorAsync(AppUser user, string code)
    {
        if (await VerifyAuthenticatorAsync(user, code)) return true;

        var redeemed = await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());

        return redeemed.Succeeded;
    }

    /// <summary>
    /// Whether a run of wrong codes has closed this account for a while.
    ///
    /// <para>Identity's lockout counter, and deliberately the same one a run of wrong passwords
    /// fills. <c>SignInManager</c> does this counting for the code at sign-in on its own; on this
    /// screen there is no sign-in to hang it off, so it is counted here — without which a stolen
    /// session cookie could walk all million six-digit codes at whatever rate the box answers and
    /// turn the second factor off. That is the one attack this screen has to survive, because
    /// everyone who reaches it already has a session.</para>
    /// </summary>
    private async Task<bool> LockedOutAsync(AppUser user) =>
        users.SupportsUserLockout && await users.IsLockedOutAsync(user);

    private async Task CountAgainstLockoutAsync(AppUser user)
    {
        if (!users.SupportsUserLockout) return;

        await users.AccessFailedAsync(user);
    }

    // ------------------------------------------------------------------ the session this ran in

    /// <summary>
    /// Re-issues this browser's cookie, and by omission ends every other one.
    ///
    /// <para>Every write above — the switch, the key reset — bumps the account's security stamp, and
    /// <c>AddDriveUnionTenancy</c> has the stamp compared on <i>every</i> request. So the moment 2FA
    /// is turned on, every outstanding cookie for this account stops working on its holder's next
    /// click. That is the right behaviour and it is the reason this is a one-liner rather than an
    /// oversight: turning on a second factor is precisely the moment somebody wants anything else
    /// still holding their session to be thrown out.</para>
    ///
    /// <para>The exception is the browser doing it, which would otherwise be thrown out too — asked
    /// for a second factor it has, on a screen it was mid-way through, for having just improved its
    /// own security. This hands it a cookie carrying the new stamp, keeping whatever «stay signed
    /// in» answer it arrived with. It is not a hole: the request that reaches this line has already
    /// produced a correct code, which is more than the session alone proved.</para>
    ///
    /// <para>Sessions already open elsewhere are not retroactively asked for a code — a cookie is a
    /// bearer credential and there is no asking it anything. They are simply ended, which is the
    /// stronger of the two answers.</para>
    /// </summary>
    private Task KeepThisBrowserSignedInAsync(AppUser user) => signInManager.RefreshSignInAsync(user);

    /// <summary>
    /// The account this cookie names, or null.
    ///
    /// <para>Null is unreachable through a real sign-in: the security stamp is validated on every
    /// request and a principal whose row has gone is rejected before a controller sees it. It is
    /// still answered rather than dereferenced, because the alternative is a 500 on the security
    /// screen for a session that is already broken.</para>
    /// </summary>
    private Task<AppUser?> CurrentUserAsync() => users.GetUserAsync(User);

    /// <summary>
    /// What to do with a session that names an account that is not there: end it, and send them to
    /// the form. Not a 403 — nobody has been refused anything, there is simply nobody to be.
    /// </summary>
    private async Task<IActionResult> SessionNamesNobody()
    {
        logger.LogWarning("A session reached /security naming a user id with no row behind it.");

        await signInManager.SignOutAsync();

        return RedirectToAction("Login", "Account", new { area = "Identity" });
    }

    private IActionResult Done(string notice, IEnumerable<string>? codes = null)
    {
        TempData["Notice"] = notice;

        if (codes is not null) TempData["RecoveryCodes"] = string.Join("\n", codes);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// A refusal, carried through a redirect rather than rendered from the POST.
    ///
    /// <para>Post-redirect-get like every other write in the panel, and here it earns its keep twice:
    /// the code that was refused is not left in a form for the back button to re-post, and a reader
    /// who refreshes after a wrong code does not send it again and spend another lockout attempt.</para>
    /// </summary>
    private IActionResult Refused(string message)
    {
        TempData["Refusal"] = message;

        return RedirectToAction(nameof(Index));
    }

    private static bool Blank(string? code) => string.IsNullOrWhiteSpace(code);

    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}

/// <summary>
/// What the security screen is drawn from.
///
/// <para>Beside its controller rather than in <c>Models/</c>, which is where the panel's other view
/// models live. That folder is a shared surface — half a dozen screens keep their models in four
/// files there and several slices are in it at once — and this record is read by exactly one action
/// and one view. A type nobody else can reach is better kept where the two things that use it
/// are.</para>
/// </summary>
/// <param name="IsOperator">Whether to say the sentence about what an operator's password unlocks.</param>
/// <param name="IsOn">The switch itself.</param>
/// <param name="SharedKey">
/// The account's TOTP secret in groups of four, and null once it is on — see
/// <c>SecurityController.ReadAsync</c> for why a live account stops showing it.
/// </param>
/// <param name="AuthenticatorUri">The <c>otpauth://</c> URI behind the «open in a code app» link.</param>
/// <param name="RecoveryCodesLeft">Unused codes in the current set. Zero when the switch is off.</param>
/// <param name="FreshRecoveryCodes">
/// A newly minted set, newline separated, on the one render that follows minting it and never again.
/// </param>
/// <param name="Notice">What just happened.</param>
/// <param name="Refusal">Why the last attempt did not.</param>
public sealed record SecurityPageViewModel(
    bool IsOperator,
    bool IsOn,
    string? SharedKey,
    string? AuthenticatorUri,
    int RecoveryCodesLeft,
    string? FreshRecoveryCodes,
    string? Notice,
    string? Refusal)
{
    /// <summary>The set as a list, so the view does not split a string in a loop.</summary>
    public IReadOnlyList<string> FreshCodes => FreshRecoveryCodes is { Length: > 0 } codes
        ? codes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];
}
