using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The form a stranger uses to tell the operator that a public link is hosting something it should
/// not.
///
/// <para>Anonymous, and it has to be: the person reporting is not a customer and will not make an
/// account to tell you your service is hosting their film. What that costs is a form anybody can
/// submit, which is why it is rate-limited like the public page and capped per link in the
/// service.</para>
///
/// <para>This replaced the string <c>abuse@yourdomain.com</c>, which was printed on every public
/// page in the product and pointed at a mailbox that did not exist. A report route nobody reads is
/// worse than none — it looks like diligence and delivers nothing.</para>
/// </summary>
[AllowAnonymous]
public sealed class AbuseController(
    IAbuseReports reports,
    IDownloadIpHasher ipHasher) : Controller
{
    [HttpGet("/d/{slug}/report")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicPage)]
    public IActionResult Report(string slug, [FromQuery(Name = "lang")] string? lang)
    {
        var language = PublicLanguageResolver.Resolve(slug is null ? null : lang, Request.Headers.AcceptLanguage.ToString());

        ViewData["Lang"] = PublicText.LangCode(language);

        // The slug is echoed back and nothing is looked up. Rendering this page for a slug that does
        // not exist is the point: a form that only appeared for real links would answer «does this
        // exist» to anybody who asked, which is what the public card's single refusal exists to stop.
        Response.Headers.CacheControl = "no-store";

        return View(
            "~/Views/Public/Report.cshtml",
            new AbuseReportViewModel(language, slug ?? string.Empty, false));
    }

    [HttpPost("/d/{slug}/report")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicPage)]

    // No antiforgery token. This form is reached by people with no session and no cookie, often from
    // a mail client's browser, and a token that had to be minted first would turn a thirty-second
    // report into a page that sometimes silently fails. What a forged submission achieves is one row
    // in a queue that is already capped per link and rate-limited per address — which is the whole
    // of the damage, and it is smaller than the reports that would never arrive.
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Report(
        string slug,
        AbuseKind kind,
        string? note,
        string? email,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = PublicLanguageResolver.Resolve(lang, Request.Headers.AcceptLanguage.ToString());

        await reports.FileAsync(
            slug ?? string.Empty,
            kind,
            note,
            email,

            // Hashed the same way a download's address is — for telling one person filing forty
            // reports apart from forty people filing one, and for nothing else.
            ipHasher.Hash(HttpContext.Connection.RemoteIpAddress),
            cancellationToken);

        ViewData["Lang"] = PublicText.LangCode(language);
        Response.Headers.CacheControl = "no-store";

        // The same page and the same sentence whatever happened — accepted, refused for an unknown
        // slug, refused because the link is already at its cap. See UiText.Abuse.ReportThanks.
        return View(
            "~/Views/Public/Report.cshtml",
            new AbuseReportViewModel(language, slug ?? string.Empty, true));
    }
}
