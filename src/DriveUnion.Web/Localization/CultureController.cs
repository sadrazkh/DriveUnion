using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Localization;

/// <summary>
/// The language switch, and the only thing in the product that writes the culture cookie.
///
/// It lives beside the mechanism rather than under Controllers/ because it has no screen of its
/// own: it takes a POST and answers with a redirect. MVC finds a controller by its type, not by its
/// folder, so <c>/Culture/Set</c> is served by the conventional route Program.cs already maps.
///
/// A POST, not a link. The switch changes something that outlives the request — a cookie the
/// visitor keeps for a year — and the panel's other state-changing control, sign-out, is a POST for
/// the same reason: a GET is followed by link prefetchers, by <c>img src</c>, and by anything that
/// crawls a page. A form also means the switch works with JavaScript off, which is the point of
/// resolving the language on the server at all.
/// </summary>
public sealed class CultureController : Controller
{
    /// <summary>
    /// Long enough that a customer sets it once. It is not a session preference — somebody who
    /// cannot read Persian cannot read Persian tomorrow either.
    /// </summary>
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    /// <param name="culture">
    /// A tag the panel actually speaks. Anything else is refused rather than ignored: the only way
    /// to send one is to write the POST by hand, and a cookie holding an unsupported tag would be
    /// resolved away silently on every request for a year with nothing to show for it.
    /// </param>
    /// <param name="returnUrl">
    /// Where the visitor was. Local addresses only — this action is reachable anonymously from the
    /// sign-in page, and an open redirect there is a phishing hop with the product's own domain in
    /// front of it.
    /// </param>
    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string? culture, string? returnUrl)
    {
        if (PanelCulture.Parse(culture) is not { } chosen) return BadRequest();

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(chosen)),
            new CookieOptions
            {
                // Path "/" and the framework's own cookie name and value shape, both on purpose:
                // this cookie is sent to /d/{slug} as well, so the public download page can be
                // taught to honour it in one line whenever that change is made. See
                // Localization/README.md.
                Path = "/",
                Expires = DateTimeOffset.UtcNow.Add(CookieLifetime),

                // Nothing in the browser reads it — the language is decided on the server before
                // any HTML exists — so there is no reason to expose it to script.
                HttpOnly = true,

                // Lax rather than Strict: arriving from an external link is exactly when somebody's
                // stored language matters most, and this cookie authorises nothing.
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,

                // A language preference the visitor asked for by name is not something a consent
                // banner gets to withhold, and marking it so keeps it out of any future policy that
                // drops non-essential cookies.
                IsEssential = true,
            });

        return returnUrl is { Length: > 0 } destination && Url.IsLocalUrl(destination)
            ? LocalRedirect(destination)
            : Redirect("/");
    }
}
