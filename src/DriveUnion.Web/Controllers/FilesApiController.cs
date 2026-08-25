using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The JSON the <c>filesTable</c> and <c>fileDetails</c> islands read.
///
/// Read-only on purpose: deletion goes through the panel's own form post, so this controller has no
/// unsafe method and therefore no CSRF surface to guard.
/// </summary>
[ApiController]
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[Route("api/files")]
public sealed class FilesApiController(
    IFileCatalog catalog,
    IOptions<DriveUnionWebOptions> options) : ControllerBase
{
    [HttpGet("")]
    public async Task<ActionResult<IReadOnlyList<FileResponse>>> List(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        // The whole list. Searching over the API is a query parameter this endpoint does not
        // document yet, and inventing one here would be an API surface nothing has agreed.
        var files = await catalog.ListAsync(tenantId, nameQuery: null, cancellationToken);
        return Ok(files.Select(FileResponse.From).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FileDetailResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var file = await catalog.GetAsync(tenantId, id, cancellationToken);
        if (file is null) return NotFound();

        var baseUrl = options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";

        return Ok(FileDetailResponse.From(file, baseUrl));
    }
}
