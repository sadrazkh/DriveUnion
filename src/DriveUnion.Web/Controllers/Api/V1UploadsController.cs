using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Web.Controllers.Api;

/// <summary>
/// Uploading over a key: the same resumable session the browser uses.
///
/// <para><b>Not a single POST.</b> A customer's program uploads the same files their browser does,
/// and the largest of those are the reason this product exists — a one-shot upload would need the
/// whole file buffered somewhere before Drive saw a byte, and would give a script no way to resume
/// after a dropped connection. It calls the same <c>IUploadCoordinator</c> the panel does, so an
/// upload is one implementation with two front doors rather than two implementations.</para>
///
/// <para><b>No antiforgery, unlike the panel's.</b> A CSRF token defends a credential the browser
/// attaches by itself; a bearer header is attached by the program holding it. There is no cookie on
/// this route to forge.</para>
/// </summary>
[ApiController]
[Route("api/v1/uploads")]
[EnableRateLimiting(DriveUnionRateLimits.Api)]
[DriveApiExceptionFilter]
public sealed class V1UploadsController(IUploadCoordinator coordinator) : ControllerBase
{
    [HttpPost("")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<IActionResult> Begin(
        [FromBody] BeginUploadPayload payload,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        // The key's owner, so bytes uploaded by a program land in the same folder that person's
        // panel uploads do. A key is somebody acting through a script, not a second kind of member.
        var result = await coordinator.BeginAsync(
            tenantId,
            User.GetUserId(),
            // A program may encrypt too, and the same three doors will refuse to hand back what it
            // stored — including this API's own content route. Accepting the header is still right:
            // dropping it would store ciphertext with nothing to open it by, which is worse than a
            // 409 somebody can read.
            new BeginUploadRequest(
                payload.FileName,
                payload.MimeType,
                payload.SizeBytes,
                payload.Encryption),
            cancellationToken);

        return Created(
            $"/api/v1/uploads/{result.SessionId}",
            new BeginUploadResponse(result.SessionId, result.ChunkSize));
    }

    /// <summary>
    /// One chunk, addressed by <c>Content-Range</c>.
    ///
    /// <para>The header is the whole protocol: <c>bytes {from}-{to}/{total}</c>, the same as the
    /// panel sends and the same as Drive's own resumable upload takes. Bytes are streamed through
    /// and never held.</para>
    /// </summary>
    [HttpPut("{id:guid}/chunk")]
    [Authorize(Policy = ApiPolicies.Write)]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Chunk(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        // The same three refusals the panel's chunk makes, word for word in effect: a range that is
        // not a concrete one, a unit that is not bytes, and a Content-Length that disagrees with it.
        // Drive acknowledges only a contiguous prefix, so a mismatch stalls the upload rather than
        // failing it — which is far harder for somebody writing a script to diagnose.
        if (!ContentRangeHeaderValue.TryParse(Request.Headers[HeaderNames.ContentRange].ToString(), out var range)
            || !range.HasRange
            || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || range.From is not { } from
            || range.To is not { } to
            || to < from)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_content_range",
                detail: "Content-Range must be 'bytes {from}-{to}/{total}' with a concrete range.");
        }

        var length = to - from + 1;

        if (Request.ContentLength is { } declared && declared != length)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "content_length_mismatch",
                detail: "Content-Length does not match the range in Content-Range.");
        }

        var progress = await coordinator.WriteChunkAsync(
            tenantId,
            id,
            Request.Body,
            from,
            length,
            cancellationToken);

        return Ok(UploadProgressResponse.From(progress));
    }

    /// <summary>
    /// Where a session got to, which is what a script asks before it resumes.
    ///
    /// <para>Read scope and not write, deliberately: asking how far an upload got changes nothing,
    /// and a monitoring key that could look but not touch is a reasonable thing for somebody to
    /// want.</para>
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<IActionResult> Progress(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var progress = await coordinator.GetProgressAsync(tenantId, id, cancellationToken);

        return Ok(UploadProgressResponse.From(progress));
    }
}
