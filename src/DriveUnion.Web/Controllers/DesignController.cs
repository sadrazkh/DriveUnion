using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The living style guide at /design.
///
/// Its job is to let someone check a pixel — a radius, an oklch() in dark mode, the height of a
/// skeleton row against a real one — without a database, without a Google account, and without
/// signing in. Gating it to Development would put it out of reach on the OVH box, which is the one
/// place with the real font, the real browser and the real screen.
///
/// It is nonetheless operator-only by default, because it is not neutral markup: it draws the
/// «همه اکانت‌ها» filter, the «اکانت» column, the daily-quota rule and «افزودن اکانت سوم — ظرفیت کل
/// به ۱۵TB می‌رسد». A customer reaching this page learns the product runs on a pool of Google
/// accounts, which §1.4 of the spec spends its length preventing everywhere else.
///
/// Set <c>DriveUnion:PublicDesignGuide</c> to open it to anonymous visitors — a switch somebody
/// flips deliberately for a designer, not a default nobody noticed.
/// </summary>
[Authorize(Policy = DriveUnionPolicies.DesignGuide)]
[Route("design")]
public sealed class DesignController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
