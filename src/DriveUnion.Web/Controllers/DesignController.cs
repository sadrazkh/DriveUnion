using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The living style guide at /design.
///
/// Anonymous and available in every environment on purpose. Its job is to let anyone check a
/// pixel — a radius, an oklch() in dark mode, the height of a skeleton row against a real one —
/// without a database, without a Google account, and without signing in. Gated to Development it
/// would be unreachable on the OVH box, which is the one place the real font, the real browser and
/// the real screen are. It renders static markup and reads nothing.
/// </summary>
[AllowAnonymous]
[Route("design")]
public sealed class DesignController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
