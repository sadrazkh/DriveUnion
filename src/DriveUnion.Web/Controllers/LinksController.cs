using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «لینک‌های اشتراک» — every link the tenant owns, in one table.
///
/// The shell has always pointed at this address, and until now it 404'd: the only listing on
/// <see cref="IShareLinkService"/> was per-file, so there was no tenant-wide query to render. There
/// is one now, and it takes the tenant as an argument like everything else in the panel — §8 of the
/// M1 design forbids a global query filter, because one would resolve every anonymous /d/{slug} to
/// <c>Guid.Empty</c>.
///
/// The comp's sticky settings aside is deliberately absent. Its slug field, password, alias and
/// download-cap slider are M4 (spec §12) and nothing on IShareLinkService could save any of them,
/// so drawing it would be a panel of controls that quietly do nothing — which is the same lesson
/// the dead nav item taught, one screen further in.
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[Route("links")]
public sealed class LinksController(IShareLinkService shareLinks) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var now = DateTimeOffset.UtcNow;
        var links = await shareLinks.ListForTenantAsync(tenantId, cancellationToken);

        var rows = links
            .Select(row => new LinkRowViewModel(
                row.StoredFileId,
                row.FileName,
                PublicLinkFormatter.Path(row.Link.Slug),
                Downloads(row.Link),
                Expiry(row.Link, now),
                LinkStatuses.Classify(row.Link, now)))
            .ToList();

        return View(new LinksPageViewModel(rows));
    }

    /// <summary>«۲۴۱/۵۰۰», or «۱۸۹/∞» where the customer set no cap.</summary>
    private static string Downloads(ShareLinkSummary link) =>
        link.MaxDownloads is { } cap
            ? UiText.Links.DownloadsOfCap(link.DownloadCount, cap)
            : UiText.Links.DownloadsUncapped(link.DownloadCount);

    private static string Expiry(ShareLinkSummary link, DateTimeOffset now) =>
        DisplayFormats.DaysUntil(link.ExpiresAt, now) switch
        {
            null => UiText.Links.ExpiryNone,
            0 => UiText.Links.StatusExpired,
            var days => UiText.Links.ExpiryDays(days.Value),
        };

    // The pool's size and its daily quota are operator figures; a customer's sidebar shows neither.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
