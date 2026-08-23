using System.Diagnostics;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    /// <summary>
    /// There is no dashboard in M1, so the root is a signpost rather than a screen. An operator has
    /// no tenant and would be refused by the panel, which is correct but useless as a landing page —
    /// they get the pool instead.
    /// </summary>
    public IActionResult Index() => User.IsOperator()
        ? RedirectToAction("Index", "Accounts")
        : RedirectToAction("Index", "Files");

    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Error() =>
        View(new ErrorViewModel(Activity.Current?.Id ?? HttpContext.TraceIdentifier));
}
