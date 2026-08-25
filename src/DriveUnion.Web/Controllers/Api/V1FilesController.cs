using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models.Api;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers.Api;

/// <summary>
/// The customer's files, over a key instead of a browser session.
///
/// <para><b>Why <c>/api/v1</c> and not the panel's own <c>/api/files</c>.</b> That one is the
/// island's back end: cookie-authenticated, antiforgery-checked, and free to change shape whenever
/// the screen does. This is a contract somebody writes a program against, so it is versioned, it is
/// bearer-only, and its shapes are records in <c>Models/Api</c> that exist to be stable rather than
/// to serve a view. Two audiences, two surfaces — the alternative is one endpoint that can never be
/// changed for the panel because a customer's script depends on it.</para>
///
/// <para>Nothing here names a Google account, a Drive id or a pool. That is the same rule the panel
/// follows and it matters more here: a JSON field is forever in a way a rendered table is not.</para>
/// </summary>
[ApiController]
[Route("api/v1/files")]
[EnableRateLimiting(DriveUnionRateLimits.Api)]
[DriveApiExceptionFilter]
public sealed class V1FilesController(
    IFileCatalog catalog,
    IFolderTree folders,
    ITags tags,
    IShareLinkService links,
    IStoredFileBytes bytes,
    IDriveClient drive,
    IOptions<DriveUnionWebOptions> options) : ControllerBase
{
    /// <summary>
    /// The workspace's files, filtered the same three ways the screen filters them.
    /// </summary>
    [HttpGet("")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<ActionResult<V1FileListResponse>> List(
        [FromQuery] Guid? folder,
        [FromQuery] string? q,
        [FromQuery] Guid? tag,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var files = await catalog.ListAsync(
            tenantId,
            new FileListFilter(folder, q, tag),
            cancellationToken);

        var labels = await tags.ForFilesAsync(tenantId, [.. files.Select(f => f.Id)], cancellationToken);

        return Ok(new V1FileListResponse(
            [.. files.Select(f => V1File.From(f, labels.TryGetValue(f.Id, out var mine) ? mine : []))]));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<ActionResult<V1FileDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var file = await catalog.GetAsync(tenantId, id, cancellationToken);

        // 404 and not 403 for another workspace's id, deliberately: the two are the same answer, or
        // the difference is a way to ask whether a file id exists.
        return file is null ? NotFound() : Ok(V1FileDetail.From(file));
    }

    /// <summary>
    /// The bytes.
    ///
    /// <para>Range is passed to Drive untouched and Drive's answer comes back, the same way
    /// <c>/d/{slug}</c> does it — so a program resuming a large download behaves. Metering is
    /// <b>not</b> applied here and that is a decision rather than an omission: the meter counts what
    /// a workspace serves to the public, and a customer pulling their own file back is not that. It
    /// is stated on <c>UiText.Capacity.TrafficCounts</c>, which is what the screen tells them.</para>
    /// </summary>
    [HttpGet("{id:guid}/content")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var file = await bytes.ResolveAsync(tenantId, id, cancellationToken);
        if (file is null) return NotFound();

        var range = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;

        var download = await drive.OpenDownloadAsync(
            file.GoogleAccountId,
            file.DriveFileId,
            range,
            cancellationToken);

        Response.StatusCode = download.IsPartial
            ? StatusCodes.Status206PartialContent
            : StatusCodes.Status200OK;

        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = file.MimeType;

        if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
        if (download.ContentLength is { } length) Response.ContentLength = length;

        await using (download)
        {
            await download.Content.CopyToAsync(Response.Body, cancellationToken);
        }

        return new EmptyResult();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        // To the trash, exactly as the panel's delete does — a key must not be a way to skip the
        // thirty days a customer's own screen promises them.
        return await catalog.DeleteAsync(tenantId, id, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>Files a file into a folder, or to the root when <c>folder</c> is absent.</summary>
    [HttpPost("{id:guid}/folder")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<IActionResult> Move(
        Guid id,
        [FromBody] V1MoveRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var moved = await folders.MoveFileAsync(tenantId, id, request?.FolderId, cancellationToken);

        return moved.Succeeded ? NoContent() : NotFound();
    }

    /// <summary>A public link, with the same optional expiry and cap the panel's API takes.</summary>
    [HttpPost("{id:guid}/links")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<ActionResult<V1Link>> CreateLink(
        Guid id,
        [FromBody] V1CreateLinkRequest? request,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var link = await links.CreateAsync(
            tenantId,
            new CreateShareLinkRequest(id, request?.ExpiresAt, request?.MaxDownloads),
            cancellationToken);

        return Ok(V1Link.From(link, PublicBase()));
    }

    [HttpDelete("{fileId:guid}/links/{linkId:guid}")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<IActionResult> RevokeLink(
        Guid fileId,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        return await links.RevokeAsync(tenantId, linkId, cancellationToken) ? NoContent() : NotFound();
    }

    // The customer sends this address to somebody else, so it is the product's own origin and not
    // whichever host the API request happened to arrive on.
    private string PublicBase() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
}
