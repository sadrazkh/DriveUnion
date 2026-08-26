using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
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
    ITags tags,
    IShareLinkService shareLinks,
    IFileEncryption encryption,
    IRemoteFetches fetches,
    IAntiforgery antiforgery,
    IOptions<DriveUnionWebOptions> options) : Controller
{
    /// <summary>
    /// How many files one press of «حذف» may take.
    ///
    /// <para>Moving a selection is one UPDATE and has no ceiling. Deleting is a Drive round trip per
    /// file — roughly a third of a second each against Google — so twenty is about six seconds of
    /// somebody watching a button, and two hundred is a request that times out with half the
    /// selection deleted and no way to tell which half. Past this the screen says so and takes
    /// none of them, rather than doing what it can and leaving the reader to work out the rest.</para>
    /// </summary>
    private const int MostDeletableAtOnce = 20;

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
        Guid? tag,
        Guid? selected,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var query = q?.Trim() is { Length: > 0 } typed ? typed : null;
        var everywhere = query is not null || tag is not null;

        Guid? here = !everywhere && folder is { } asked
            && await folders.ExistsAsync(tenantId, asked, cancellationToken)
                ? asked
                : null;

        var now = DateTimeOffset.UtcNow;
        var files = await catalog.ListAsync(
            tenantId,
            new FileListFilter(here, query, tag),
            cancellationToken);

        var labels = await tags.ListAsync(tenantId, cancellationToken);
        var onFiles = await tags.ForFilesAsync(tenantId, [.. files.Select(f => f.Id)], cancellationToken);

        // One row per encrypted file on this page and nothing for the rest, which is nearly all of
        // them. Membership draws the padlock; the value is the size, because what the catalogue
        // holds for a locked file is the ciphertext and that is not the file the customer has.
        var lengths = await encryption.PlaintextLengthsAsync(
            tenantId, [.. files.Select(f => f.Id)], cancellationToken);

        // One read of the tree, used three ways: the folders drawn as rows, the breadcrumb above
        // them, and the «move to…» list in the detail panel. Reading it once is not an optimisation
        // — it is what stops the three of them disagreeing inside one response.
        var choices = await folders.ChoicesAsync(tenantId, excludingSubtreeOf: null, cancellationToken);
        var pathOf = choices.ToDictionary(c => c.Id, c => c.Path);

        var rows = files
            .Select(file => new FileRowViewModel(
                file.Id,
                file.Name,
                DisplayFormats.Bytes(
                    lengths.TryGetValue(file.Id, out var plain) ? plain : file.SizeBytes),
                DisplayFormats.Relative(file.ModifiedAt, now),
                file.ActiveLinkCount,
                file.Id == selected,
                !everywhere
                    ? null
                    : file.FolderId is { } id && pathOf.TryGetValue(id, out var path)
                        ? path
                        : UiText.Files.RootFolder,
                onFiles.TryGetValue(file.Id, out var mine)
                    ? [.. mine.Select(t => new TagViewModel(t.Id, t.Name, 0))]
                    : [],
                lengths.ContainsKey(file.Id)))
            .ToList();

        var folderRows = everywhere
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

            // The panel may be open on a file the table is not drawing — a search that has since
            // narrowed past it — so this asks about the one file rather than reusing the page's map.
            //
            // The whole header and not just the length, because the panel is where an owner shares a
            // locked file: doing that means opening it here, in this browser, and re-wrapping its key
            // for the link. Their own file, in their own authenticated panel, and the same header the
            // public page already publishes to anyone holding a link.
            if (file is not null)
            {
                detail = ToDetail(
                    file,
                    now,
                    await encryption.ForFileAsync(tenantId, selectedId, cancellationToken));
            }
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
            folderTargets,
            [.. labels.Select(t => new TagViewModel(t.Id, t.Name, t.FileCount))],
            tag));
    }

    /// <summary>
    /// Everything a selection can have done to it, behind one button name.
    ///
    /// <para>One action rather than three, because the three share a form: the checkboxes are the
    /// selection and the button that was pressed is the verb. Three forms would mean three copies of
    /// the same checkbox list, or a script to keep them in step — and this screen works without one.</para>
    /// </summary>
    [HttpPost("selection")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Selection(
        string? act,
        Guid[]? ids,
        Guid? destination,
        string? label,
        string? q,
        Guid? folder,
        Guid? tag,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var chosen = ids ?? [];

        if (chosen.Length == 0)
        {
            TempData["Notice"] = UiText.Files.NothingSelected;
            return RedirectToAction(nameof(Index), new { q, folder, tag });
        }

        switch (act)
        {
            case "move":
            {
                var moved = await folders.MoveFilesAsync(tenantId, chosen, destination, cancellationToken);

                TempData["Notice"] = moved.Succeeded
                    ? UiText.Files.FilesMoved(moved.Contains)
                    : UiText.Files.NotFound;

                // Follows the selection into the folder it was sent to, the same as moving one file:
                // landing where they went is the confirmation that they arrived.
                return RedirectToAction(nameof(Index), new { q, folder = destination, tag });
            }

            case "tag" when label is not null:
            {
                var made = await tags.EnsureAsync(tenantId, label, cancellationToken);

                if (!made.Succeeded || made.TagId is not { } tagId)
                {
                    TempData["Notice"] = made.Outcome == TagOutcome.TooMany
                        ? UiText.Files.TooManyTags(Core.Storage.Tag.MaxPerTenant)
                        : UiText.Files.TagNeedsAName;

                    return RedirectToAction(nameof(Index), new { q, folder, tag });
                }

                var applied = await tags.ApplyAsync(tenantId, chosen, tagId, cancellationToken);
                TempData["Notice"] = UiText.Files.TagApplied(label.Trim(), applied.Affected);

                return RedirectToAction(nameof(Index), new { q, folder, tag });
            }

            case "untag" when destination is null && tag is { } current:
            {
                var removed = await tags.RemoveAsync(tenantId, chosen, current, cancellationToken);
                TempData["Notice"] = UiText.Files.TagRemoved(removed.Affected);

                return RedirectToAction(nameof(Index), new { q, folder, tag });
            }

            case "delete":
            {
                if (chosen.Length > MostDeletableAtOnce)
                {
                    // None of them, rather than the first twenty. Deleting half a selection and
                    // saying so is worse than deleting none and saying why: the reader cannot see
                    // which half without going and looking.
                    TempData["Notice"] = UiText.Files.TooManyToDelete(MostDeletableAtOnce, chosen.Length);
                    return RedirectToAction(nameof(Index), new { q, folder, tag });
                }

                var deleted = 0;
                foreach (var id in chosen)
                {
                    if (await catalog.DeleteAsync(tenantId, id, cancellationToken)) deleted++;
                }

                TempData["Notice"] = UiText.Files.FilesDeleted(deleted);

                return RedirectToAction(nameof(Index), new { q, folder, tag });
            }

            default:
                TempData["Notice"] = UiText.Files.NotFound;
                return RedirectToAction(nameof(Index), new { q, folder, tag });
        }
    }

    [HttpPost("tags/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTag(Guid id, Guid? folder, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await tags.DeleteAsync(tenantId, id, cancellationToken);

        TempData["Notice"] = result.Succeeded ? UiText.Files.TagRetired(result.Affected) : UiText.Files.NotFound;

        return RedirectToAction(nameof(Index), new { folder });
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
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var pulls = await fetches.ListAsync(tenantId, cancellationToken);

        return View(new UploadPageViewModel(
            Tokens(),
            [.. pulls.Select(ToFetchRow)],
            TempData["Notice"] as string,
            TempData["Error"] as string));
    }

    /// <summary>
    /// Asks the server to go and get a file, so the customer's own connection is not involved.
    ///
    /// <para>Queued rather than done: a 40 GB pull is not something a form post waits for, and the
    /// browser that asked for it is expected to be closed long before it finishes. That is the whole
    /// point of the feature.</para>
    /// </summary>
    [HttpPost("fetch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(string? url, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await fetches.StartAsync(tenantId, User.GetUserId(), url ?? string.Empty, cancellationToken);

        if (result.Started)
        {
            TempData["Notice"] = UiText.Files.FetchQueued;
        }
        else
        {
            // Every refusal is something the customer can see and fix, so it is a sentence rather
            // than a status code. «queue_full» is not about the link at all, which is why it is a
            // detail beside the refusal rather than another value of it.
            TempData["Error"] = result.Detail == "queue_full"
                ? UiText.Files.FetchQueueFull
                : result.Refusal switch
                {
                    RemoteSourceRefusal.UnsupportedScheme => UiText.Files.FetchBadScheme,
                    RemoteSourceRefusal.CarriesCredentials => UiText.Files.FetchHasCredentials,
                    _ => UiText.Files.FetchMalformed,
                };
        }

        return RedirectToAction(nameof(Upload));
    }

    [HttpPost("fetch/{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelFetch(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        if (await fetches.CancelAsync(tenantId, id, cancellationToken))
        {
            TempData["Notice"] = UiText.Files.FetchCancelled;
        }

        return RedirectToAction(nameof(Upload));
    }

    private static RemoteFetchRowViewModel ToFetchRow(RemoteFetchView fetch)
    {
        var live = fetch.Status is RemoteFetchStatus.Queued or RemoteFetchStatus.Running;

        return new RemoteFetchRowViewModel(
            fetch.Id,
            fetch.Url,

            // The name is only known once the source has been asked. While that is still coming,
            // saying so is right; on a row that has already failed it reads as something still in
            // progress — and the address underneath already says which fetch this is.
            fetch.FileName ?? (live ? UiText.Files.FetchNameUnknown : "—"),
            fetch.Status switch
            {
                RemoteFetchStatus.Queued => UiText.Files.FetchStateQueued,
                RemoteFetchStatus.Running => UiText.Files.FetchStateRunning,
                RemoteFetchStatus.Completed => UiText.Files.FetchStateDone,
                RemoteFetchStatus.Cancelled => UiText.Files.FetchStateStopped,
                _ => UiText.Files.FetchStateFailed,
            },
            live,

            // «—» while the size is still unknown, which it is until the source has been asked. A
            // zero there would read as a file of no size rather than a question not yet put.
            fetch.SizeBytes > 0
                ? $"{DisplayFormats.Bytes(fetch.BytesFetched)} / {DisplayFormats.Bytes(fetch.SizeBytes)}"
                : "—",
            fetch.FailureReason);
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
    /// <param name="note">
    /// What the sender wants whoever opens the link to read, or nothing.
    ///
    /// <para>Bound loose and not validated here: the service trims it, cuts it to the column and
    /// turns an empty one into null. A 400 on a sentence that ran three characters long would lose
    /// somebody the link they were making.</para>
    /// </param>
    /// <param name="key">
    /// The file's content key re-wrapped for this link, as JSON, for a locked file — and absent for
    /// every other file and for a browser with no script.
    ///
    /// <para>A string and not a bound model, because it arrives as one form field written by the
    /// sharing island. Unparseable is refused rather than dropped: a link created without the key it
    /// was meant to have is one that hands out the owner's own wrapped key instead, which quietly
    /// widens what the recipient can open.</para>
    /// </param>
    [HttpPost("{id:guid}/links")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLink(
        Guid id,
        string? note,
        string? key,
        string? q,
        Guid? folder,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        LinkKeyMaterial? material = null;

        if (key?.Trim() is { Length: > 0 } payload)
        {
            try
            {
                material = JsonSerializer.Deserialize<LinkKeyMaterial>(payload, PanelJson);
            }
            catch (JsonException)
            {
                material = null;
            }

            if (material is null || !material.IsWellFormed)
            {
                TempData["Notice"] = UiText.Files.ShareKeyRefused;
                return RedirectToAction(nameof(Index), new { q, folder, selected = id });
            }
        }

        var link = await shareLinks.CreateAsync(
            tenantId,
            new CreateShareLinkRequest(id, null, null, note, material),
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

    /// <param name="header">
    /// How to open the file when it is locked, and null when it is not — which is also how this
    /// method knows which of the two it is looking at.
    /// </param>
    private FileDetailViewModel ToDetail(FileDetail file, DateTimeOffset now, EncryptionHeader? header)
    {
        var baseUrl = PublicBaseUrl();

        return new FileDetailViewModel(
            file.Id,
            file.Name,
            DisplayFormats.Bytes(header?.PlaintextLength ?? file.SizeBytes),
            DisplayFormats.FileKind(file.Name, file.MimeType),
            DisplayFormats.PanelDateTime(file.CreatedAt),
            [.. file.Links.Select(link => ToLink(link, baseUrl, now))],
            header is not null,
            header is null ? null : JsonSerializer.Serialize(header, PanelJson));
    }

    /// <summary>
    /// camelCase, because the only reader is the sharing island and the format's field names live in
    /// <c>Scripts/crypto/format.ts</c>. Nothing on this side reads it back.
    /// </summary>
    private static readonly JsonSerializerOptions PanelJson = new(JsonSerializerDefaults.Web);

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
            link.IsActive,
            link.HasOwnKey);
    }

    // The customer copies this address and sends it to somebody else, so it is the product's own
    // origin and not whichever host this panel request arrived on. The request is only a fallback
    // for a deployment that has not configured one yet.
    private string PublicBaseUrl() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
}
