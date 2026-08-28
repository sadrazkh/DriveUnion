using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
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
    IDeletionQueue deletions,
    ITags tags,
    IShareLinkService shareLinks,
    IFileEncryption encryption,
    IRemoteFetches fetches,
    IFileLocks locks,
    IStoredFileBytes bytes,
    IDriveClient drive,
    ITrafficMeter traffic,
    IEgressAllowance allowance,
    IAntiforgery antiforgery,
    IOptions<DriveUnionWebOptions> options) : Controller
{
    // There is no longer a ceiling on how many files one press of «حذف» may take, and the constant
    // that held one — MostDeletableAtOnce = 20 — is gone rather than raised.
    //
    // The argument for it was sound and is now answered rather than overridden: deleting was a Drive
    // round trip per file, so two hundred was a request that timed out with half the selection gone
    // and no way to tell which half. IDeletionQueue does the visible half in one statement — the rows
    // stamped, the links revoked, the deadline written — and owes the round trips to a worker. What
    // used to be the reason for a limit is now the part nobody is waiting for.

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
                    await encryption.ForFileAsync(tenantId, selectedId, cancellationToken),
                    await encryption.SealedByAsync(tenantId, selectedId, cancellationToken));
            }
        }

        // One indexed query, and nearly always no rows: a workspace has a clean-up running for the
        // minute or two after somebody deletes a folder full of files, and never otherwise.
        var tidying = await deletions.LiveAsync(tenantId, cancellationToken);

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
            tag,
            [.. tidying.Select(DeletionProgressViewModel.FromJob)]));
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
                // Queued rather than looped over, whatever the size. The count is what actually went
                // and not what was ticked — ids that are not this workspace's and files already in
                // the trash are never matched — which is the number the notice has to say anyway.
                var deleted = await deletions.DeleteFilesAsync(tenantId, chosen, cancellationToken);

                TempData["Notice"] = UiText.Files.FilesDeleted(deleted.Files);

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
    /// The other verb: the folder and everything under it.
    ///
    /// <para>A separate route rather than a flag on the one above, because the two destroy different
    /// things — that one destroys a name and this one destroys a customer's files — and a route
    /// somebody can post to by accident should not be able to become the wrong one of those by a
    /// missing form field.</para>
    ///
    /// <para>It returns as soon as the rows are stamped, which is the whole point: what is left is a
    /// Drive move per file that nobody is waiting for. The screen says so while it runs — see
    /// <see cref="Index"/>.</para>
    /// </summary>
    /// <param name="folder">
    /// Where to land afterwards, which is the folder <i>above</i> the one being deleted. Redirecting
    /// into it would be redirecting into a folder that no longer exists — the reader would be
    /// silently dropped at the root with no breadcrumb explaining how they got there.
    /// </param>
    [HttpPost("folders/{id:guid}/delete-everything")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFolderAndContents(
        Guid id,
        Guid? folder,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await deletions.DeleteFolderAsync(tenantId, id, cancellationToken);

        TempData["Notice"] = result.Found
            ? UiText.Files.FolderAndContentsDeleted(result.Files)
            : UiText.Files.NotFound;

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
    /// <param name="secret">
    /// What to lock the arriving file with, or blank to store it as it comes.
    ///
    /// <para>Used in this request and not written down: the service derives from it, wraps a content
    /// key, and keeps only the wrapped form. It never reaches the queue row or a log line.</para>
    /// </param>
    public async Task<IActionResult> Fetch(
        string? url,
        string? secret,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var result = await fetches.StartAsync(
            tenantId,
            User.GetUserId(),
            url ?? string.Empty,
            secret?.Trim() is { Length: > 0 } typed ? typed : null,
            cancellationToken);

        if (WantsJson())
        {
            return Json(new
            {
                started = result.Started,
                error = result.Started ? null : RefusalText(result),
            });
        }

        if (result.Started)
        {
            TempData["Notice"] = UiText.Files.FetchQueued;
        }
        else
        {
            // Every refusal is something the customer can see and fix, so it is a sentence rather
            // than a status code. «queue_full» is not about the link at all, which is why it is a
            // detail beside the refusal rather than another value of it.
            TempData["Error"] = RefusalText(result);
        }

        return RedirectToAction(nameof(Upload));
    }

    /// <summary>
    /// Why a link was refused, in one place so the form and the island cannot word it differently.
    ///
    /// <para>«queue_full» is not about the link at all — the customer's next action is to wait rather
    /// than to fix what they typed — which is why it is a detail beside the refusal rather than
    /// another value of it.</para>
    /// </summary>
    private static string RefusalText(RemoteFetchStartResult result) =>
        result.Detail == "queue_full"
            ? UiText.Files.FetchQueueFull
            : result.Refusal switch
            {
                RemoteSourceRefusal.UnsupportedScheme => UiText.Files.FetchBadScheme,
                RemoteSourceRefusal.CarriesCredentials => UiText.Files.FetchHasCredentials,
                _ => UiText.Files.FetchMalformed,
            };

    [HttpPost("fetch/{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelFetch(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var stopped = await fetches.CancelAsync(tenantId, id, cancellationToken);

        if (WantsJson()) return Json(new { stopped });

        if (stopped) TempData["Notice"] = UiText.Files.FetchCancelled;

        return RedirectToAction(nameof(Upload));
    }

    /// <summary>
    /// This workspace's link fetches, for the upload screen to poll.
    ///
    /// <para>The island draws these in the same list as the browser's own uploads, so it needs the
    /// same thing it has for those: a figure that moves. A fetch is carried out by a worker in
    /// another process-wide loop, so there is nothing to observe locally and the only honest source
    /// is the row.</para>
    /// </summary>
    [HttpGet("fetches")]
    public async Task<IActionResult> Fetches(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var rows = await fetches.ListAsync(tenantId, cancellationToken);

        return Json(new
        {
            fetches = rows.Select(f =>
            {
                var view = ToFetchRow(f);

                return new
                {
                    id = f.Id,
                    url = f.Url,
                    name = view.Name,
                    status = f.Status.ToString(),
                    statusText = view.StatusText,
                    live = view.IsLive,
                    progress = view.ProgressText,

                    // Percent rather than a pair of byte counts, because the bar wants one number
                    // and «unknown until the source has been asked» is a real state here.
                    percent = f.SizeBytes > 0
                        ? Math.Min(100d, f.BytesFetched * 100d / f.SizeBytes)
                        : 0d,
                    known = f.SizeBytes > 0,
                    error = view.FailureReason,
                };
            }),
        });
    }

    /// <summary>
    /// The owner's own bytes, for a player on this screen.
    ///
    /// <para><b>This is the first cookie-authenticated byte route in the product</b>, and it was
    /// absent on purpose rather than by omission: a customer reached their own file by making a
    /// share link, which is metered and capped like every other link. That worked and it made
    /// watching a film of your own into a link somebody else could also have. What it never was is
    /// a hole, and adding this route must not open one.</para>
    ///
    /// <para>So it meters and it caps, exactly as <c>/api/v1</c> does and for the reason written
    /// there: an exemption here would not be «your own files are free», it would be «your own files
    /// are free through the panel», which is the same bypass with a different front door. The
    /// customer who watches a film has spent the traffic it took to watch it, and that is the same
    /// arithmetic as if they had sent themselves a link.</para>
    ///
    /// <para>Range is passed to Drive untouched, which is what makes seeking work — and what makes a
    /// seek cost only the part that was watched.</para>
    /// </summary>
    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var file = await bytes.ResolveAsync(tenantId, id, cancellationToken);
        if (file is null) return NotFound();

        // Ciphertext, served as ciphertext. The player on this screen is the one that knows what to
        // do with it — the service worker decrypts a segment at a time and hands plaintext to the
        // media element — so this route's job is to be honest about what it is serving and let the
        // browser do the rest. Refusing here would make an encrypted film unplayable by its owner.
        var standing = await allowance.ReadAsync(tenantId, cancellationToken);

        if (standing.IsOverAllowance)
        {
            Response.Headers.RetryAfter = EgressWindow.NextResetHeader();

            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "over_traffic_allowance",
                detail: "This workspace has used its monthly traffic allowance. "
                    + "It resumes at the start of the next calendar month.");
        }

        var range = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;

        var download = await drive.OpenDownloadAsync(
            file.GoogleAccountId, file.DriveFileId, range, cancellationToken);

        Response.StatusCode = download.IsPartial
            ? StatusCodes.Status206PartialContent
            : StatusCodes.Status200OK;

        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = file.MimeType;

        // Never stored. It is the customer's own file and this is their own browser, but a shared
        // cache between them and it is not theirs — and the encrypted case is plaintext's ciphertext,
        // which the service worker will decrypt and must not find on disk afterwards.
        Response.Headers.CacheControl = "no-store";

        if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
        if (download.ContentLength is { } length) Response.ContentLength = length;

        var sent = 0L;

        try
        {
            await using (download)
            {
                await EgressCopy.CopyAsync(
                    download.Content, Response.Body, copied => sent = copied, cancellationToken);
            }

            return new EmptyResult();
        }
        finally
        {
            // Not the request's token: when the reader is the one who stopped watching it is already
            // cancelled, and the bytes they took would go uncounted for the one reason that is not a
            // failure at all.
            if (sent > 0) await traffic.RecordAsync(tenantId, sent, CancellationToken.None);
        }
    }

    /// <summary>
    /// Locks a file that is already stored, in place.
    ///
    /// <para><b>The passphrase does not come here, and that is the difference between this and the
    /// link-upload path.</b> That one takes what the customer typed and derives on the server, which
    /// it can defend — it is fetching the file itself, so it is holding the plaintext anyway. The
    /// defence does not extend to the passphrase: a customer uses one secret for everything, so a
    /// server that has seen it once could open every file they ever locked <i>in their browser</i>.
    /// This route takes a header the browser built and the content key for this one file, which is a
    /// key to a file the server is about to read regardless and to nothing else.</para>
    ///
    /// <para>JSON only, and by design: there is no no-script version of deriving a key. Without a
    /// bundle the button is not drawn, which is the honest answer — a form that posted a passphrase
    /// would be the weaker protocol wearing this one's clothes.</para>
    /// </summary>
    /// <param name="key">
    /// Base64, the raw content key. It is used for this job and held in memory for its duration —
    /// never written to a row, never logged. See <c>ContentKeyring</c>.
    /// </param>
    [HttpPost("{id:guid}/lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lock(
        Guid id,
        [FromForm] EncryptionHeaderForm header,
        [FromForm] string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (User.GetTenantId() is not { } tenantId) return Forbid();

        byte[] contentKey;
        try
        {
            contentKey = Convert.FromBase64String(key ?? string.Empty);
        }
        catch (FormatException)
        {
            return Json(new { started = false, error = UiText.Locking.RefusalMalformed });
        }

        // 32 bytes, because the format is AES-256 and nothing else will do. Checked here rather than
        // discovered by the sealing loop, where it would be a failed job instead of a refused button.
        if (contentKey.Length != 32)
        {
            return Json(new { started = false, error = UiText.Locking.RefusalMalformed });
        }

        var result = await locks.StartAsync(
            tenantId, User.GetUserId(), id, header.ToHeader(), contentKey, cancellationToken);

        return Json(new
        {
            started = result.Refusal is FileLockRefusal.None,
            error = result.Refusal switch
            {
                FileLockRefusal.None => null,
                FileLockRefusal.UnknownFile => UiText.Locking.RefusalUnknownFile,
                FileLockRefusal.AlreadyLocked => UiText.Locking.RefusalAlreadyLocked,
                FileLockRefusal.AlreadyLocking => UiText.Locking.RefusalAlreadyLocking,
                FileLockRefusal.NoRoom => UiText.Locking.RefusalNoRoom,
                _ => UiText.Locking.RefusalMalformed,
            },
        });
    }

    /// <summary>
    /// Whether the caller is the upload island rather than a browser posting a form.
    ///
    /// <para>One pair of routes for both, because they do exactly the same thing and a second pair
    /// would be a second place for the refusals to be worded. The form still works with no script at
    /// all — that is the whole reason it is a form.</para>
    /// </summary>
    private bool WantsJson() =>
        Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

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
    private FileDetailViewModel ToDetail(
        FileDetail file,
        DateTimeOffset now,
        EncryptionHeader? header,
        SealedBy? sealedBy)
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

            // The plaintext length, which for an unlocked file is what is stored. The lock card
            // seals into segments and has to know how many before it has read a byte.
            header?.PlaintextLength ?? file.SizeBytes,

            // What the panel may play. Encryption is not consulted — unlike the public card, which
            // is talking to a stranger with no key: this is the owner, and the worker decrypts a
            // segment at a time once they have typed their passphrase.
            Previews.OnceUnlocked(file.MimeType) switch
            {
                PreviewKind.Video => "video",
                PreviewKind.Audio => "audio",
                _ => string.Empty,
            },
            sealedBy switch
            {
                SealedBy.Client => UiText.Files.SealedByClient,
                SealedBy.Server => UiText.Files.SealedByServer,
                _ => null,
            },
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
