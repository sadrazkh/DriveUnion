using System.Diagnostics;
using System.Globalization;
using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The panel's root, and it is a screen now rather than a signpost.
///
/// <para><b>Two dashboards behind one address, exactly as <c>/plans</c> already is.</b> The
/// operator's draws the pool — each Google account, what it holds, who is near their ceiling, and
/// what is failing. The customer's draws their own: storage against their plan, their files, their
/// live links, what has been downloaded and what their trash is still holding. They share a route
/// and not a view model, and neither record can carry the other's figures: M1 §1.4 says a customer
/// must never learn which account holds their file nor that a pool exists, and a screen that decided
/// that with an <c>if</c> inside one view is one edit away from deciding it wrongly.</para>
///
/// <para><b>Why the dashboard lives here rather than on a controller of its own.</b> Signing in ends
/// with <c>RedirectToAction("Index", "Home")</c>, so <c>Home/Index</c> has to stay an endpoint that
/// link generation can find — and <c>/</c> has to stay the address, because the sidebar's first item
/// and every «go home» instinct already point at it. A second controller claiming <c>/</c> would
/// either put two endpoints on one path or leave that redirect generating a URL for an action that
/// no longer exists. So the action stays and stops redirecting, and the views it renders live in
/// <c>Views/Dashboard/</c> because that is what they are.</para>
/// </summary>
public sealed class HomeController : Controller
{
    private const string CustomerView = "~/Views/Dashboard/Customer.cshtml";

    private const string OperatorView = "~/Views/Dashboard/Operator.cshtml";

    /// <summary>
    /// The dashboard, for whichever of the two audiences asked for it.
    ///
    /// <para>The readers are resolved here rather than through the constructor, and that is about
    /// <see cref="Error"/> rather than about this action. This controller also renders the page the
    /// exception handler sends people to, and a constructor dependency would make that page
    /// unbuildable in exactly the situation it exists for — a container that cannot compose a
    /// dashboard would answer a failed request with a second failure and no page at all. Asked for
    /// with <c>GetRequiredService</c> and not <c>GetService</c>: a missing registration is a fault to
    /// be shouted about on this one route, not a blank screen.</para>
    /// </summary>
    [Authorize]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (User.IsOperator())
        {
            var pool = await HttpContext.RequestServices
                .GetRequiredService<IOperatorDashboard>()
                .ReadAsync(cancellationToken);

            SetOperatorShell(pool);

            return View(OperatorView, new OperatorDashboardPageViewModel(pool));
        }

        // An operator has no workspace and a customer has one; a principal with neither is a session
        // the panel cannot scope, and it is refused rather than shown an empty screen.
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var dashboard = await HttpContext.RequestServices
            .GetRequiredService<ICustomerDashboard>()
            .ReadAsync(tenantId, cancellationToken);

        // The claim named a workspace and the row is not there. A fault rather than a screen of
        // zeroes, which is the same answer /plans gives: a panel that renders zeroes for a workspace
        // that does not exist is how a broken session reads as a customer with an empty account.
        if (dashboard is null) return NotFound();

        return View(CustomerView, new CustomerDashboardPageViewModel(dashboard));
    }

    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Error() =>
        View(new ErrorViewModel(Activity.Current?.Id ?? HttpContext.TraceIdentifier));

    /// <summary>
    /// The operator's shell: the pool summary under the brand, and nothing else that is not already
    /// there.
    ///
    /// <para>The account count and the pool's size are written from the same two quantities
    /// «اکانت‌های گوگل» writes them from — every account and every account's total — so the line
    /// under the brand does not change as the reader moves between the two screens.</para>
    ///
    /// <para><c>DailyQuotaUsedGb</c> is deliberately left unset. Nothing in this product counts what
    /// a Google account has uploaded today, and the shell draws a skeleton where a figure has not
    /// been read rather than a plausible one. Filling it in from anything available here would be
    /// inventing the number the whole card is about.</para>
    /// </summary>
    private void SetOperatorShell(OperatorDashboard dashboard) =>
        ViewData[ShellContext.Key] = new ShellContext
        {
            AccountSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{dashboard.Accounts.Count} accounts · {DisplayFormats.Bytes(dashboard.Accounts.Sum(a => a.QuotaTotalBytes))}"),
            UserName = User.Identity?.Name,
            UserRole = UiText.Shell.RoleOperator,
        };

    // The pool's size and its daily quota are operator figures; a customer's sidebar shows neither.
    // The capacity card above the name is left for the shell to ask about, so it is the same card
    // here as on every other screen a customer opens.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = UiText.Shell.RoleUser,
    };
}
