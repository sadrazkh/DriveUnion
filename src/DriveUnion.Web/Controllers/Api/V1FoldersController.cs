using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models.Api;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DriveUnion.Web.Controllers.Api;

/// <summary>
/// The customer's folder tree, over a key.
///
/// <para>The tree is ours and is not mirrored into Google Drive — see <c>Folder</c> — so everything
/// here is a row, and a program filing a thousand files costs no Drive call at all.</para>
/// </summary>
[ApiController]
[Route("api/v1/folders")]
[EnableRateLimiting(DriveUnionRateLimits.Api)]
[DriveApiExceptionFilter]
public sealed class V1FoldersController(IFolderTree folders) : ControllerBase
{
    /// <summary>The folders directly inside one, or inside the root when <c>parent</c> is absent.</summary>
    [HttpGet("")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<ActionResult<V1FolderListResponse>> List(
        [FromQuery] Guid? parent,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var children = await folders.ChildrenAsync(tenantId, parent, cancellationToken);

        return Ok(new V1FolderListResponse(
            [.. children.Select(f => new V1Folder(f.Id, f.Name, f.FileCount, f.SubfolderCount))]));
    }

    [HttpPost("")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<ActionResult<V1Folder>> Create(
        [FromBody] V1CreateFolderRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();
        if (User.GetUserId() is not { } userId) return Forbid();

        var made = await folders.CreateAsync(
            tenantId,
            userId,
            request?.ParentId,
            request?.Name ?? string.Empty,
            cancellationToken);

        // The refusals a tree makes, as the statuses they are: 409 for a name a sibling already has,
        // 422 for one that would loop or nest too deep, 400 for no name at all. A program needs to
        // tell «try another name» from «stop» without reading English.
        return made.Outcome switch
        {
            FolderOutcome.Done => Ok(new V1Folder(made.FolderId!.Value, request!.Name!.Trim(), 0, 0)),
            FolderOutcome.NameEmpty => BadRequest(new { error = "name_required" }),
            FolderOutcome.NameTaken => Conflict(new { error = "name_taken" }),
            FolderOutcome.TooDeep => UnprocessableEntity(new { error = "too_deep" }),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Deletes a folder with nothing live in it.
    ///
    /// <para>Empty-only and permanent, the same as the screen's — and refused with a count when it
    /// is not, because deleting a full folder means a Drive round trip per descendant inside one
    /// request. An API that quietly did what the panel refuses would be a way round a limit that
    /// exists for the customer's benefit.</para>
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = ApiPolicies.Write)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var deleted = await folders.DeleteAsync(tenantId, id, cancellationToken);

        return deleted.Outcome switch
        {
            FolderOutcome.Done => NoContent(),
            FolderOutcome.NotEmpty => Conflict(new { error = "not_empty", contains = deleted.Contains }),
            _ => NotFound(),
        };
    }
}
