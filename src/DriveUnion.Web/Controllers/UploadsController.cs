using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// Browser → OVH → Google, one chunk at a time.
///
/// The browser never sees the Drive session URI: it is a bearer capability over the operator's
/// account. What crosses this controller is bytes, and they are never held — see
/// <see cref="Chunk"/>.
/// </summary>
[ApiController]
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[AutoValidateAntiforgeryToken]
[DriveApiExceptionFilter]
[Route("api/uploads")]
public sealed class UploadsController(IUploadCoordinator coordinator) : ControllerBase
{
    [HttpPost("")]
    public async Task<IActionResult> Begin(
        [FromBody] BeginUploadPayload payload,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        // Whose folder the bytes land in, taken from the principal and never from the payload.
        var result = await coordinator.BeginAsync(
            tenantId,
            User.GetUserId(),
            new BeginUploadRequest(payload.FileName, payload.MimeType, payload.SizeBytes),
            cancellationToken);

        var response = new BeginUploadResponse(result.SessionId, result.ChunkSize);
        return CreatedAtAction(nameof(Progress), new { id = result.SessionId }, response);
    }

    /// <summary>
    /// One chunk, streamed straight through.
    ///
    /// The request body is handed to the coordinator as a forward-only stream and nothing between
    /// Kestrel and Google is allowed to hold it: no model binding (the action takes no body
    /// parameter and the form providers are removed), no size limit (a 32 MiB chunk is far past the
    /// default 30 MB), no buffering. A 96 GB upload that gets spooled anywhere is a 96 GB bug.
    /// </summary>
    [HttpPut("{id:guid}/chunk")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Chunk(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        if (!ContentRangeHeaderValue.TryParse(Request.Headers.ContentRange.ToString(), out var contentRange)
            || !contentRange.HasRange
            || !contentRange.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From is not { } from
            || contentRange.To is not { } to
            || to < from)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "invalid_content_range",
                detail: "Content-Range must be 'bytes {from}-{to}/{total}' with a concrete range.");
        }

        var length = to - from + 1;

        // A declared length that disagrees with the range means the client and the server would
        // write different byte counts into the session, and Drive only acknowledges a contiguous
        // prefix — the upload would stall rather than fail.
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

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Progress(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var progress = await coordinator.GetProgressAsync(tenantId, id, cancellationToken);
        return Ok(UploadProgressResponse.From(progress));
    }
}
