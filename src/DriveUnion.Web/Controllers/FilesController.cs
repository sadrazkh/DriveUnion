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
    IFolderTree folders,
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
    /// <param name="folder">
    /// The folder being browsed, or absent for the workspace's root.
    ///
    /// <para>Dropped rather than refused when it names a folder this workspace does not have: a
    /// stale bookmark or a folder somebody deleted should land the reader at the root with their
    /// files in front of them, not on a 404. It is dropped while searching too, because a search is
    /// the whole workspace.</para>
    /// </param>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q,
        Guid? folder,
        Guid? selected,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var query = q?.Trim() is { Length: > 0 } typed ? typed : null;

        Guid? here = query is null && folder is { } asked
            && await folders.ExistsAsync(tenantId, asked, cancellationToken)
                ? asked
                : null;

        var now = DateTimeOffset.UtcNow;
        var files = await catalog.ListAsync(tenantId, here, query, cancellationToken);

        // One read of the tree, used three ways: the folders drawn as rows, the breadcrumb above
        // them, and the «move to…» list in the detail panel. Reading it once is not an optimisation
        // — it is what stops the three of them disagreeing inside one response.
        var choices = await folders.ChoicesAsync(tenantId, excludingSubtreeOf: null, cancellationToken);
        var pathOf = choices.ToDictionary(c => c.Id, c => c.Path);

        var rows = files
            .Select(file => new FileRowViewModel(
                file.Id,
                file.Name,
                DisplayFormats.Bytes(file.SizeBytes),
                DisplayFormats.Relative(file.ModifiedAt, now),
                file.ActiveLinkCount,
                file.Id == selected,
                query is null
                    ? null
                    : file.FolderId is { } id && pathOf.TryGetValue(id, out var path)
                        ? path
                        : UiText.Files.RootFolder))
            .ToList();

        var folderRows = query is not null
            ? []
            : (await folders.ChildrenAsync(tenantId, here, cancellationToken))
                .Select(f => new FolderRowViewModel(f.Id, f.Name, f.FileCount, f.SubfolderCount))
                .ToList();

        var crumbs = new List<CrumbViewModel> { new(null, UiText.Files.RootFolder, here is null) };

        if (here is { } current)
        {
            var path = await folders.PathAsync(tenantId, current, cancellationToken);
            crumbs.AddRange(path.Select(c => new CrumbViewModel(c.Id, c.Name, c.Id == current)));
        }

        var moveTargets = new List<FolderChoiceViewModel> { new(null, UiText.Files.RootFolder) };
        moveTargets.AddRange(choices.Select(c => new FolderChoiceViewModel(c.Id, c.Path)));

        // A second walk, and worth the query: moving the folder you are standing in must not offer
        // its own descendants, and dropping one entry from the list above would leave every one of
        // its children in it.
        var folderTargets = new List<FolderChoiceViewModel> { new(null, UiText.Files.RootFolder) };

        if (here is { } moving)
        {
            var allowed = await folders.ChoicesAsync(tenantId, moving, cancellationToken);
            folderTargets.AddRange(allowed.Select(c => new FolderChoiceViewModel(c.Id, c.Path)));
        }

        // Read by id and not from the list above, which is what lets a selected file keep its panel
        // open while a search that does not match it narrows the table. The row loses its highlight
        // because it is no longer drawn; the file is still this tenant's and still theirs to act on.
        FileDetailViewModel? detail = null;
        if (selected is { } selectedId)
        {
            var file = await catalog.GetAsync(tenantId, selectedId, cancellationToken);
            if (file is not null) detail = ToDetail(file, now);
        }

        return View(new FilesPageViewModel(
            rows,
            detail,
            Tokens(),
            TempData["Notice"] as string,
            query,
            here,
            folderRows,
            crumbs,
            moveTargets,
            folderTargets));
    }

    [HttpPost("folders")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFolder(
        string? name,
        Guid? folder,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();
        if (User.GetUserId() is not { } userId) return Forbid();

        var result = await folders.CreateAsync(tenantId, userId, folder, name ?? string.Empty, cancellationToken);

        TempData["Notice"] = Say(result, name);

        // Back to where they were, not into what they just made. Somebody filing things makes a
        // folder and then drags into it; being moved inside it means navigating back out first.
        return RedirectToAction(nameof(Index), new { folder });
    }

    [HttpPost("folders/{id:guid}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameFolder(
        Guid id,
        string? name,
        Guid? folder,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await folders.RenameAsync(tenantId, id, name ?? string.Empty, cancellationToken);

        TempData["Notice"] = Say(result, name);

        return RedirectToAction(nameof(Index), new { folder });
    }

    [HttpPost("folders/{id:guid}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveFolder(
        Guid id,
        Guid? destination,
        Guid? folder,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await folders.MoveAsync(tenantId, id, destination, cancellationToken);

        TempData["Notice"] = Say(result, null);

        return RedirectToAction(nameof(Index), new { folder });
    }

    [HttpPost("folders/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFolder(Guid id, Guid? folder, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await folders.DeleteAsync(tenantId, id, cancellationToken);

        TempData["Notice"] = Say(result, null);

        return RedirectToAction(nameof(Index), new { folder });
    }

    /// <summary>
    /// Files a file, or takes it back to the root.
    ///
    /// <para>No Drive call. Where a customer keeps a file and where the operator's pool keeps its
    /// bytes are two different questions — see <c>Folder</c> — so this is one column on one row.</para>
    /// </summary>
    [HttpPost("{id:guid}/file-into")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveFileInto(
        Guid id,
        Guid? destination,
        string? q,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await folders.MoveFileAsync(tenantId, id, destination, cancellationToken);

        TempData["Notice"] = result.Succeeded ? UiText.Files.FileMoved : UiText.Files.NotFound;

        // Follows the file rather than staying put: the reader just said where it belongs, and
        // landing on the folder they sent it to is the confirmation that it arrived.
        return RedirectToAction(nameof(Index), new { q, folder = destination, selected = id });
    }

    /// <summary>The one place a folder outcome becomes a sentence, so nine call sites cannot drift.</summary>
    private static string Say(FolderResult result, string? name) => result.Outcome switch
    {
        FolderOutcome.Done => UiText.Files.FolderDone,
        FolderOutcome.NameEmpty => UiText.Files.FolderNeedsAName,
        FolderOutcome.NameTaken => UiText.Files.FolderNameTaken(name?.Trim() ?? string.Empty),
        FolderOutcome.NotEmpty => UiText.Files.FolderNotEmpty(result.Contains),
        FolderOutcome.WouldLoop => UiText.Files.FolderWouldLoop,
        FolderOutcome.TooDeep => UiText.Files.FolderTooDeep,
        _ => UiText.Files.NotFound,
    };

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
    public async Task<IActionResult> Delete(Guid id, string? q, Guid? folder, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var deleted = await catalog.DeleteAsync(tenantId, id, cancellationToken);
        TempData["Notice"] = deleted ? UiText.Files.Deleted : UiText.Files.NotFound;

        // No `selected`: the file it named is gone, and a detail panel for it would be an empty box
        // beside a table that no longer lists it.
        return RedirectToAction(nameof(Index), new { q, folder });
    }

    /// <summary>
    /// A link with no expiry and no cap, which is what the panel's «ساخت لینک» means. Expiry, the
    /// download cap and the rest of the settings panel are the API's business.
    /// </summary>
    [HttpPost("{id:guid}/links")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLink(Guid id, string? q, Guid? folder, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var link = await shareLinks.CreateAsync(
            tenantId,
            new CreateShareLinkRequest(id, null, null),
            cancellationToken);

        TempData["Notice"] = UiText.Files.LinkCreated(PublicLinkFormatter.Display(PublicBaseUrl(), link.Slug));

        return RedirectToAction(nameof(Index), new { q, folder, selected = id });
    }

    [HttpPost("{fileId:guid}/links/{linkId:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeLink(Guid fileId, Guid linkId, string? q, Guid? folder, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var revoked = await shareLinks.RevokeAsync(tenantId, linkId, cancellationToken);
        TempData["Notice"] = revoked ? UiText.Files.LinkRevoked : UiText.Files.LinkNotFound;

        return RedirectToAction(nameof(Index), new { q, folder, selected = fileId });
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
