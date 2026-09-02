using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Web.Areas.Identity.Models;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Areas.Identity.Controllers;

/// <summary>
/// The only way into the panel, and the only way out of it.
///
/// It lives in an area so that <c>/Identity/Account/Login</c> is a name the cookie handler can be
/// pointed at without competing with the product's own <c>/Accounts</c> route — which is the Google
/// pool and has nothing to do with signing in.
///
/// There is no sign-up. Tenant creation is M5 (spec §12); in M1 an operator is seeded and everybody
/// else is attached to a tenant by hand, so a registration form here would only be able to make
/// accounts that cannot open a single page.
///
/// The one exception is <see cref="Setup"/>, which is not a sign-up: it exists only while the panel
/// has no operator at all, it can create exactly one account, and it is gone the moment it has.
/// </summary>
[Area("Identity")]
public sealed class AccountController(
    SignInManager<AppUser> signInManager,
    IOptions<IdentityOptions> identityOptions,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
    IWebHostEnvironment environment,
    TimeProvider clock,
    ILogger<AccountController> logger) : Controller
{
    /// <summary>
    /// Rendered by two actions — <see cref="Setup"/> at its own address, and <see cref="Login"/>
    /// when there is nobody to sign in as.
    /// </summary>
    private const string SetupViewName = "Setup";

    /// <param name="signIn">
    /// Asks for the sign-in form even when the panel has no operator. The seeder can create a tenant
    /// user without one (<c>DriveUnion:Seed:TenantSlug</c> and <c>TenantUserEmail</c> with no
    /// <c>OperatorEmail</c>), and that person must not be walled out by a setup screen that is not
    /// theirs to complete. It suppresses nothing but this redirection — the setup route is gated on
    /// the database, not on this.
    /// </param>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        string? returnUrl,
        bool signIn,
        CancellationToken cancellationToken)
    {
        // A signed-in visitor asking for the sign-in form is asking for the panel. Home is the
        // signpost that knows which half of it they belong to.
        if (User.Identity?.IsAuthenticated == true)
        {
            return LandingPage(returnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = returnUrl;

        // What the second step said on its way back here — that it expired, or that the account is
        // now locked. Through TempData because those are redirects: the alternative is rendering the
        // sign-in form in answer to a POST that carried a code, which re-posts the code on refresh.
        if (TempData["Refusal"] is string refusal)
        {
            ModelState.AddModelError(string.Empty, refusal);
        }

        // Every challenge in the panel lands here, so this is where "there is nobody to be" has to
        // be answered. A sign-in form on an empty database is a locked door with no key cut for it.
        //
        // Rendered rather than redirected to /Identity/Account/Setup: this address is what the cookie
        // handler names and what the whole product links to, and a 302 on it would break callers
        // that reasonably expect the sign-in address to answer with a page.
        if (!signIn && !await FirstOperator.ExistsAsync(signInManager.UserManager, cancellationToken))
        {
            return View(SetupViewName, NewSetupModel());
        }

        return View(NewLoginModel());
    }

    /// <summary>
    /// The first-run screen, at its own address so the form has somewhere to post to.
    ///
    /// Anonymous, and it creates an administrator — so the check that matters is the one on the
    /// request that writes, not the one on the request that drew the form. Both ask the database,
    /// and both answer 404 once there is an operator: the route is gone, not disabled.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Setup(CancellationToken cancellationToken)
    {
        if (await FirstOperator.ExistsAsync(signInManager.UserManager, cancellationToken))
        {
            return NotFound();
        }

        SetShell();

        return View(NewSetupModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ActionName(nameof(Setup))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetupConfirmed(
        SetupViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Asked again, on this request, before anything is read out of the body. A form saved from
        // an earlier visit, a second browser tab and a hand-written POST all arrive here and are all
        // answered by the same query.
        if (await FirstOperator.ExistsAsync(signInManager.UserManager, cancellationToken))
        {
            return NotFound();
        }

        SetShell();
        Describe(model);

        if (!ModelState.IsValid)
        {
            return SetupAgain(model);
        }

        var result = await FirstOperator.CreateAsync(
            signInManager.UserManager,
            model.Email.Trim(),
            model.Password,
            clock.GetUtcNow(),
            cancellationToken);

        switch (result.Outcome)
        {
            case FirstOperatorOutcome.AlreadyProvisioned:
                // Two setup requests overlapped and this is the one that lost — the database refused
                // the duplicate key rather than this code refusing the request, which is why the
                // window between the check above and the insert is not a window at all. Nothing was
                // written here, so the honest answer is the same 404 a late visitor gets.
                logger.LogWarning(
                    "A first-run setup arrived after the operator already existed; nothing was created.");

                return NotFound();

            case FirstOperatorOutcome.Refused:
                // Identity's own descriptions — "Passwords must be at least 10 characters." — and not
                // a sentence of ours saying the password was no good. A first screen that refuses
                // without saying what it wants is worse than the command line it replaces.
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return SetupAgain(model);

            default:
                break;
        }

        var user = result.User!;

        // Deliberately not persistent, and now the only sign-in in the panel that is not.
        //
        // Everywhere else the answer to "stay signed in?" is yes, because the form asks it, ticks it
        // and explains it — a thirty-day cookie somebody was shown and can undo in one click. This
        // screen has no such control; it has two password boxes and a button. Making it persistent
        // would mint the longest-lived credential in the product for the account that owns every
        // Google account and every workspace, on a request where nobody was ever offered the choice.
        //
        // The cost of the other answer is one sign-in. This screen runs exactly once, at a desk,
        // with the password still in the operator's hands, and the form it sends them to is the one
        // that does offer the choice — so the phone this whole change is about never meets this
        // path at all. ExpireTimeSpan bounds the ticket either way.
        await signInManager.SignInAsync(user, isPersistent: false);

        logger.LogInformation("The first operator {Email} was created from the setup screen.", user.Email);

        // The pool is what an operator came for, and the panel's own screens would refuse them:
        // operator staff have no tenant. area = "" leaves this area, without which the redirect
        // resolves to nothing.
        return RedirectToAction("Index", "Accounts", new { area = "" });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(model);

        SetShell();
        Describe(model);
        ViewData["ReturnUrl"] = returnUrl;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // The panel signs in by email, so the lookup is by email rather than by user name — the two
        // are the same for a seeded account and need not stay that way.
        var user = await signInManager.UserManager.FindByEmailAsync(model.Email);

        if (user is null)
        {
            return Refuse(model, UiText.Identity.BadCredentials);
        }

        var result = await signInManager.PasswordSignInAsync(
            user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return Refuse(model, UiText.Identity.LockedOut);
        }

        // A correct password with a second factor pending is a step, not a refusal — and this is
        // where the whole feature was broken. PasswordSignInAsync answers RequiresTwoFactor by *not*
        // succeeding, so the branch below read it as a wrong password: switching the second factor
        // on locked the account for ever and told its owner they were mistyping, with nowhere in the
        // product to type the code they had just set up.
        //
        // After the lockout check on purpose: a locked account is locked whatever else is true of it.
        if (result.RequiresTwoFactor)
        {
            // Nothing about who they are travels in the URL. Identity holds the half-finished
            // sign-in in its own two-factor cookie and reads it back from there — an id in a query
            // string would be an account name in a browser history and in every referrer after it.
            return RedirectToAction(nameof(TwoFactor), new { model.RememberMe, returnUrl });
        }


        if (!result.Succeeded)
        {
            // One sentence for a wrong password and for an address with no account. The difference
            // between the two answers is a list of who has an account here.
            return Refuse(model, UiText.Identity.BadCredentials);
        }

        // The cookie carries the claims now, but User on *this* request is still the anonymous
        // principal the request arrived with, so the landing page is decided from the row.
        var hasWorkspace = user.IsOperator || (user.TenantId is { } tenantId && tenantId != Guid.Empty);

        if (!hasWorkspace)
        {
            // Worth a log line: the account is real and the password was right, so the missing piece
            // is that nobody has attached this person to a tenant.
            logger.LogInformation(
                "{Email} signed in with neither a tenant nor operator rights.", user.Email);

            return RedirectToAction(nameof(AccessDenied));
        }

        return LandingPage(returnUrl);
    }

    /// <summary>
    /// The second step, with the code from the authenticator app.
    ///
    /// <para>Reachable only with Identity's half-finished sign-in in hand, which a correct password
    /// issues and nothing else does. Somebody who opens this address cold is sent back to the
    /// password rather than shown a code box that could not work — and the same check guards the
    /// POST, because a form that renders is not a form that may be replayed an hour later.</para>
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> TwoFactor(bool rememberMe, string? returnUrl)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
        {
            return Expired(returnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = returnUrl;

        return View(new TwoFactorViewModel { RememberMe = rememberMe, ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ActionName(nameof(TwoFactor))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorConfirmed(TwoFactorViewModel model)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
        {
            // The step expired while a phone was being unlocked. Back to the password, because there
            // is nothing left here for a code to attach itself to.
            return Expired(model.ReturnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = model.ReturnUrl;

        // Authenticator apps show the six digits in two groups, and people type what they see. The
        // space and the dash are stripped here rather than refused, because rejecting a code that
        // was read off the screen correctly is the most annoying possible way to be right.
        var code = TypedCode(model.Code);

        if (code.Length == 0)
        {
            // Not counted against the lockout: there was no guess in it. See TwoFactorViewModel.Code
            // for why an empty box and a wrong code get the one sentence between them.
            ModelState.AddModelError(string.Empty, UiText.Security.CodeRequired);

            return View(model);
        }

        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            code,
            model.RememberMe,

            // rememberClient: false, deliberately. The «do not ask on this browser again» cookie is
            // a second factor that a stolen laptop already has, and there is no screen in the panel
            // to revoke one from. Two-step sign-in here means every sign-in, on every browser.
            rememberClient: false);

        if (result.IsLockedOut)
        {
            // The same lock and the same sentence a run of wrong passwords earns, said at the form
            // that can be started again rather than at this one, which cannot.
            return LockedOut(model.ReturnUrl);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, UiText.Security.BadCode);

            return View(model);
        }

        logger.LogInformation("A second step was answered with an app code.");

        return LandingPage(model.ReturnUrl);
    }

    /// <summary>
    /// The same step for somebody whose phone is gone, at its own address so that the two forms can
    /// each say one thing. The link between them is on both screens.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> RecoveryCode(bool rememberMe, string? returnUrl)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
        {
            return Expired(returnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = returnUrl;

        return View(new RecoveryCodeViewModel { RememberMe = rememberMe, ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ActionName(nameof(RecoveryCode))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecoveryCodeConfirmed(RecoveryCodeViewModel model)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
        {
            return Expired(model.ReturnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = model.ReturnUrl;

        var code = TypedRecoveryCode(model.Code);

        if (code.Length == 0)
        {
            ModelState.AddModelError(string.Empty, UiText.Security.CodeRequired);

            return View(model);
        }

        var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(code);

        if (result.IsLockedOut)
        {
            return LockedOut(model.ReturnUrl);
        }

        if (!result.Succeeded)
        {
            // One sentence for a code that was never issued and for one that has already been spent.
            // Telling those apart would say which of the ten are still good, to somebody who has not
            // yet proved they own the account.
            ModelState.AddModelError(string.Empty, UiText.Security.BadRecoveryCode);

            return View(model);
        }

        logger.LogInformation("A second step was answered with a recovery code, which is now spent.");

        // Past the security screen rather than straight to the panel, and past it whatever the
        // returnUrl said. The reader has one fewer way back in than they had this morning, and this
        // is the only screen in the product that can give it back — a notice on a page they were
        // going to anyway would be read after the moment it was useful, if at all.
        TempData["Notice"] = UiText.Security.RecoveryCodeSpent;

        return RedirectToAction("Index", "Security", new { area = "" });
    }

    /// <summary>
    /// An app code with the grouping people copy off a screen taken back out of it. Six digits, so
    /// every space and dash in it is decoration somebody's eye added.
    /// </summary>
    private static string TypedCode(string? code) => (code ?? string.Empty)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Trim();

    /// <summary>
    /// A recovery code, tidied — and the dash left exactly where it is.
    ///
    /// <para>Identity issues these as <c>xxxxx-xxxxx</c> and stores the dash as part of the code, so
    /// the strip that is right for six digits would turn every correct recovery code into a wrong
    /// one. Discovered by a test, not by reading: the two look like the same kind of string and are
    /// not, and the failure would have been «my recovery codes do not work» from somebody who has
    /// already lost their phone.</para>
    /// </summary>
    private static string TypedRecoveryCode(string? code) => (code ?? string.Empty)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Trim();

    /// <summary>
    /// Back to the password, carrying the destination, when the half-finished sign-in is gone.
    ///
    /// <para>A redirect and not a rendered form: this address is reached by a POST as well, and a
    /// sign-in page drawn in answer to one is a page that re-posts a code on refresh.</para>
    /// </summary>
    private IActionResult Expired(string? returnUrl)
    {
        TempData["Refusal"] = UiText.Security.ChallengeExpired;

        return RedirectToAction(nameof(Login), new { returnUrl, signIn = true });
    }

    private IActionResult LockedOut(string? returnUrl)
    {
        TempData["Refusal"] = UiText.Identity.LockedOut;

        return RedirectToAction(nameof(Login), new { returnUrl, signIn = true });
    }

    /// <summary>
    /// A confirmation page rather than a link that signs you out on sight. The panel shell has no
    /// sign-out control of its own, so this address is where a session is ended from.
    /// </summary>
    [HttpGet]
    [Authorize]
    public IActionResult Logout()
    {
        SetShell();
        return View();
    }

    [HttpPost]
    [Authorize]
    [ActionName(nameof(Logout))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogoutConfirmed()
    {
        var name = User.Identity?.Name;

        await signInManager.SignOutAsync();
        logger.LogInformation("Signed out {Email}.", name);

        return RedirectToAction(nameof(Login));
    }

    /// <summary>
    /// Where a 403 lands. <c>[Authorize]</c> and not <c>[AllowAnonymous]</c>: someone who is not
    /// signed in has not been refused anything yet, and should see the sign-in form instead of a
    /// card explaining a permission they were never evaluated for.
    /// </summary>
    [HttpGet]
    [Authorize]
    public IActionResult AccessDenied()
    {
        SetShell();

        return View(new AccessDeniedViewModel(
            HasWorkspace: User.GetTenantId() is not null,
            IsOperator: User.IsOperator()));
    }

    private IActionResult Refuse(LoginViewModel model, string message)
    {
        ModelState.AddModelError(string.Empty, message);
        model.Password = string.Empty;

        return View(model);
    }

    private SetupViewModel NewSetupModel()
    {
        var model = new SetupViewModel();
        Describe(model);

        return model;
    }

    private LoginViewModel NewLoginModel()
    {
        var model = new LoginViewModel();
        Describe(model);

        return model;
    }

    /// <summary>
    /// Fills in what the sign-in form says about staying signed in.
    ///
    /// <para>The same rule <see cref="Rules"/> follows for the password policy: read from the
    /// options the deployment is actually running with, so the sentence cannot promise a window the
    /// cookie handler is not granting. It is the one place the panel tells somebody how long a
    /// credential is about to be left on the machine they are sitting at, and being wrong about that
    /// is worse than being silent.</para>
    /// </summary>
    private void Describe(LoginViewModel model)
    {
        var window = cookieOptions.Get(IdentityConstants.ApplicationScheme).ExpireTimeSpan;

        // Floored at a day rather than rounded to nothing. A deployment that chose twelve hours
        // would otherwise have the form promise «for 0 days», which reads as a bug in the panel
        // rather than as a short session somebody configured on purpose.
        model.StaySignedInDays = Math.Max(1, (int)Math.Round(window.TotalDays));
    }

    /// <summary>
    /// Fills in what the page says about the password, over anything the request supplied. These
    /// three values are not the visitor's to choose, and on a POST they arrive from a body that
    /// anybody can write.
    /// </summary>
    private void Describe(SetupViewModel model)
    {
        var password = identityOptions.Value.Password;

        model.MinimumPasswordLength = password.RequiredLength;
        model.PasswordRules = Rules(password);

        // Development only, and the check is the environment rather than a configuration flag: a
        // flag can be set in production by whoever edits appsettings, and this decides whether an
        // unauthenticated page offers a credential.
        model.OfferGeneratedPassword = environment.IsDevelopment();
    }

    /// <summary>
    /// Identity's policy in the panel's own prose. Read from the options rather than transcribed, so
    /// the screen keeps telling the truth if Program.cs changes what it requires.
    ///
    /// The counts go through <see cref="Numerals"/> and not <c>PersianDigits</c> directly: they are
    /// figures set in a sentence, so they are Persian digits in Persian and Latin in English.
    /// </summary>
    private static List<string> Rules(PasswordOptions password)
    {
        var rules = new List<string>
        {
            UiText.Identity.PasswordMinimumLength(password.RequiredLength),
        };

        if (password.RequireUppercase) rules.Add(UiText.Identity.PasswordUppercase);
        if (password.RequireLowercase) rules.Add(UiText.Identity.PasswordLowercase);
        if (password.RequireDigit) rules.Add(UiText.Identity.PasswordDigit);
        if (password.RequireNonAlphanumeric) rules.Add(UiText.Identity.PasswordSymbol);

        if (password.RequiredUniqueChars > 1)
        {
            rules.Add(UiText.Identity.PasswordDistinctCharacters(password.RequiredUniqueChars));
        }

        return rules;
    }

    /// <summary>
    /// The setup form again, with both password boxes emptied. A refused password that is rendered
    /// back into the HTML is a credential in the page source, in the browser's back-forward cache
    /// and in anything between here and the browser that keeps response bodies.
    /// </summary>
    private IActionResult SetupAgain(SetupViewModel model)
    {
        model.Password = string.Empty;
        model.ConfirmPassword = string.Empty;

        return View(SetupViewName, model);
    }

    /// <summary>
    /// The requested page when it is one of ours, and the Home signpost otherwise.
    /// <c>area = ""</c> is not decoration — without it the redirect stays inside this area and
    /// resolves to nothing.
    /// </summary>
    private IActionResult LandingPage(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction("Index", "Home", new { area = "" });

    // The shell draws a quota bar and an account summary that belong to the operator's pool. A
    // sign-in page has no business reporting either, and the layout renders skeletons for what it
    // is not given — so this deliberately supplies only who is signing in, or nobody.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.Identity?.IsAuthenticated == true
            ? (User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser)
            : null,
    };
}
