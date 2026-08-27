using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The page a navigation gets when there is no network.
///
/// <para><b>Why it is a page on the server and not markup inside the worker.</b> Every user-visible
/// string in this product comes out of <c>UiText</c> and is chosen by <c>PanelCulture</c>, and a
/// page written into <c>wwwroot/sw.js</c> would be a second place the product's Persian is spelled
/// — the only one nothing renders in both languages, so the only one that could quietly drift. The
/// worker fetches this address once, at install, with the culture cookie on the request, and keeps
/// what comes back.</para>
///
/// <para><b>Anonymous, and identical for everybody.</b> That is a constraint rather than a
/// convenience: this is the one response in the product that is written to a phone's disk, so
/// anything on it that varied by who asked would be that person's data left on a device. It draws
/// no sidebar, no identity, no antiforgery token and nothing from the catalogue — the whole panel
/// shell is deliberately absent, and <c>Views/Offline/Index.cshtml</c> sets <c>Layout = null</c>
/// rather than trusting the shell to stay empty.</para>
/// </summary>
[AllowAnonymous]
public sealed class OfflineController : Controller
{
    [HttpGet("/offline")]
    public IActionResult Index()
    {
        // no-store, for the same reason the manifest is no-store: the response varies by the
        // culture cookie, and a shared cache holding one language's copy and handing it to everybody
        // is the failure that is hardest to see and slowest to expire.
        //
        // It does not stop the service worker keeping this page. The Cache API is not the HTTP cache
        // and does not read Cache-Control — which is the behaviour wanted here and is worth writing
        // down, because it looks like a contradiction.
        Response.Headers.CacheControl = "no-store";

        return View();
    }
}
