using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The owner's side of <c>/d/{slug}</c>: create a link, list a file's links, revoke one.
///
/// Every call is tenant-scoped. A link id that belongs to somebody else is a 404 rather than a 403,
/// because a 403 confirms that the id exists.
/// </summary>
[ApiController]
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[AutoValidateAntiforgeryToken]
[Route("api/share-links")]
public sealed class ShareLinksController(
    IShareLinkService shareLinks,
    IOptions<DriveUnionWebOptions> options) : ControllerBase
{
    [HttpPost("")]
    public async Task<IActionResult> Create(
        [FromBody] CreateShareLinkPayload payload,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        if (payload.StoredFileId == Guid.Empty)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_file",
                detail: "storedFileId is required.");
        }

        // A link created already expired is a support ticket waiting to happen: it renders the same
        // "no longer available" card as a revoked one, with nothing to say why.
        if (payload.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_expiry",
                detail: "expiresAt must be in the future.");
        }

        var summary = await shareLinks.CreateAsync(
            tenantId,
            new CreateShareLinkRequest(payload.StoredFileId, payload.ExpiresAt, payload.MaxDownloads),
            cancellationToken);

        var response = ShareLinkResponse.From(summary, PublicBaseUrl());
        return Created(response.Url, response);
    }

    [HttpGet("")]
    public async Task<ActionResult<IReadOnlyList<ShareLinkResponse>>> List(
        [FromQuery] Guid fileId,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        if (fileId == Guid.Empty)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_file",
                detail: "fileId is required.");
        }

        var baseUrl = PublicBaseUrl();
        var links = await shareLinks.ListForFileAsync(tenantId, fileId, cancellationToken);

        return Ok(links.Select(link => ShareLinkResponse.From(link, baseUrl)).ToList());
    }

    /// <summary>
    /// Revoke, not delete: the row stays so the download history behind it keeps a parent, and the
    /// slug stays taken so it can never be handed to a different file.
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var revoked = await shareLinks.RevokeAsync(tenantId, id, cancellationToken);
        return revoked ? NoContent() : NotFound();
    }

    private string PublicBaseUrl() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
}
