using DriveUnion.Infrastructure.Identity;
using DriveUnion.Web.Areas.Identity.Models;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

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
/// </summary>
[Area("Identity")]
public sealed class AccountController(
    SignInManager<AppUser> signInManager,
    ILogger<AccountController> logger) : Controller
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl)
    {
        // A signed-in visitor asking for the sign-in form is asking for the panel. Home is the
        // signpost that knows which half of it they belong to.
        if (User.Identity?.IsAuthenticated == true)
        {
            return LandingPage(returnUrl);
        }

        SetShell();
        ViewData["ReturnUrl"] = returnUrl;

        return View(new LoginViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl)
    {
        ArgumentNullException.ThrowIfNull(model);

        SetShell();
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
            return Refuse(model, "ایمیل یا گذرواژه درست نیست.");
        }

        var result = await signInManager.PasswordSignInAsync(
            user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return Refuse(
                model,
                "به دلیل تلاش‌های ناموفق پیاپی، این حساب موقتاً قفل شده است. کمی بعد دوباره تلاش کنید.");
        }

        if (!result.Succeeded)
        {
            // One sentence for a wrong password and for an address with no account. The difference
            // between the two answers is a list of who has an account here.
            return Refuse(model, "ایمیل یا گذرواژه درست نیست.");
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
            ? (User.IsOperator() ? "اپراتور" : "کاربر")
            : null,
    };
}
