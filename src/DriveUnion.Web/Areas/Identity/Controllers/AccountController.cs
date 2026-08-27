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
