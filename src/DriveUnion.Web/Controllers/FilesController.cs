using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «فایل‌ها» — the list and the sticky detail panel.
///
/// Rendered whole on the server and working without script: creating a link, revoking one and
/// deleting a file are form posts, and the islands enhance that rather than replace it. The tenant
/// comes from the principal's claim and is passed explicitly into every call, which is the whole
/// isolation strategy of §8 in one argument.
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[Route("files")]
public sealed class FilesController(
    IFileCatalog catalog,
    IShareLinkService shareLinks,
    IAntiforgery antiforgery,
    IOptions<DriveUnionWebOptions> options) : Controller
{
    /// <summary>
    /// The list, and the shell's search box lands here.
    ///
    /// <para><c>q</c> is the name the header's form has always submitted. It was submitted to an
    /// action that did not take it, so every search returned the whole list and the box read as a
    /// control that worked — the worst of the three possible states, because a reader who searches
    /// for a file they own, sees everything come back, and concludes the file is not there.</para>
    ///
    /// <para>Trimmed to null so that <c>?q=</c> and a box full of spaces are the unfiltered list
    /// rather than a search for nothing, and so the view has one thing to test rather than two.</para>
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, Guid? selected, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var query = q?.Trim() is { Length: > 0 } typed ? typed : null;

        var now = DateTimeOffset.UtcNow;
        var files = await catalog.ListAsync(tenantId, query, cancellationToken);

        var rows = files
            .Select(file => new FileRowViewModel(
                file.Id,
                file.Name,
                DisplayFormats.Bytes(file.SizeBytes),
                DisplayFormats.Relative(file.ModifiedAt, now),
                file.ActiveLinkCount,
                file.Id == selected))
            .ToList();

        // Read by id and not from the list above, which is what lets a selected file keep its panel
        // open while a search that does not match it narrows the table. The row loses its highlight
        // because it is no longer drawn; the file is still this tenant's and still theirs to act on.
        FileDetailViewModel? detail = null;
        if (selected is { } selectedId)
        {
            var file = await catalog.GetAsync(tenantId, selectedId, cancellationToken);
            if (file is not null) detail = ToDetail(file, now);
        }

        return View(new FilesPageViewModel(rows, detail, Tokens(), TempData["Notice"] as string, query));
    }

    /// <summary>
    /// The shell's primary button points here. The panel itself is one island — a chunked upload is
    /// not something a form post can do — so this page is a mount point and an honest explanation
    /// of what it needs.
    /// </summary>
    [HttpGet("upload")]
    public IActionResult Upload()
    {
        SetShell();
        return View(Tokens());
    }

    /// <param name="q">
    /// The search the reader was looking at, handed back so the redirect returns them to it.
    ///
    /// <para>On the three writes below rather than only on <see cref="Index"/>, because a redirect
    /// that drops it is the same defect from the other side: search, delete the file you found, and
    /// the screen answers by showing you everything else you own.</para>
    /// </param>
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, string? q, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var deleted = await catalog.DeleteAsync(tenantId, id, cancellationToken);
        TempData["Notice"] = deleted ? UiText.Files.Deleted : UiText.Files.NotFound;

        // No `selected`: the file it named is gone, and a detail panel for it would be an empty box
        // beside a table that no longer lists it.
        return RedirectToAction(nameof(Index), new { q });
    }

    /// <summary>
    /// A link with no expiry and no cap, which is what the panel's «ساخت لینک» means. Expiry, the
    /// download cap and the rest of the settings panel are the API's business.
    /// </summary>
    [HttpPost("{id:guid}/links")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLink(Guid id, string? q, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var link = await shareLinks.CreateAsync(
            tenantId,
            new CreateShareLinkRequest(id, null, null),
            cancellationToken);

        TempData["Notice"] = UiText.Files.LinkCreated(PublicLinkFormatter.Display(PublicBaseUrl(), link.Slug));

        return RedirectToAction(nameof(Index), new { q, selected = id });
    }

    [HttpPost("{fileId:guid}/links/{linkId:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeLink(Guid fileId, Guid linkId, string? q, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var revoked = await shareLinks.RevokeAsync(tenantId, linkId, cancellationToken);
        TempData["Notice"] = revoked ? UiText.Files.LinkRevoked : UiText.Files.LinkNotFound;

        return RedirectToAction(nameof(Index), new { q, selected = fileId });
    }

    private AntiforgeryTokenViewModel Tokens()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new AntiforgeryTokenViewModel(tokens.HeaderName ?? string.Empty, tokens.RequestToken ?? string.Empty);
    }

    // The pool's size and its daily quota are operator figures; a customer's sidebar shows neither.
    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };

    private FileDetailViewModel ToDetail(FileDetail file, DateTimeOffset now)
    {
        var baseUrl = PublicBaseUrl();

        return new FileDetailViewModel(
            file.Id,
            file.Name,
            DisplayFormats.Bytes(file.SizeBytes),
            DisplayFormats.FileKind(file.Name, file.MimeType),
            DisplayFormats.PanelDateTime(file.CreatedAt),
            [.. file.Links.Select(link => ToLink(link, baseUrl, now))]);
    }

    private static ShareLinkViewModel ToLink(ShareLinkSummary link, string baseUrl, DateTimeOffset now)
    {
        var downloads = link.MaxDownloads is { } cap
            ? UiText.Files.DownloadsOfCap(link.DownloadCount, cap)
            : UiText.Files.Downloads(link.DownloadCount);

        var days = DisplayFormats.DaysUntil(link.ExpiresAt, now);
        var expiry = days switch
        {
            null => UiText.Files.NoExpiry,
            0 => UiText.Files.Expired,
            _ => UiText.Files.ExpiresInDays(days.Value),
        };

        return new ShareLinkViewModel(
            link.Id,
            link.Slug,
            PublicLinkFormatter.Display(baseUrl, link.Slug),
            PublicLinkFormatter.Absolute(baseUrl, link.Slug),
            downloads,
            expiry,
            link.IsActive);
    }

    // The customer copies this address and sends it to somebody else, so it is the product's own
    // origin and not whichever host this panel request arrived on. The request is only a fallback
    // for a deployment that has not configured one yet.
    private string PublicBaseUrl() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
}
