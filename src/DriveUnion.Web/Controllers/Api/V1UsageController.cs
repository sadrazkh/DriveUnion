using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Models.Api;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DriveUnion.Web.Controllers.Api;

/// <summary>
/// What the workspace has spent and what it may spend.
///
/// <para>The endpoint P9 made possible and P11 makes useful. A program that uploads on a schedule is
/// the thing most likely to walk into a ceiling, and «you are over» arriving as a refused request
/// with no way to have checked first is the worst version of that — so both halves of both limits
/// are readable, in bytes, before anything is attempted.</para>
/// </summary>
[ApiController]
[Route("api/v1/usage")]
[EnableRateLimiting(DriveUnionRateLimits.Api)]
[DriveApiExceptionFilter]
public sealed class V1UsageController(
    ITenantPlanService plans,
    ITrafficMeter traffic,
    TimeProvider clock) : ControllerBase
{
    [HttpGet("")]
    [Authorize(Policy = ApiPolicies.Read)]
    public async Task<ActionResult<V1Usage>> Get(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var plan = await plans.GetAsync(tenantId, cancellationToken);
        if (plan is null) return NotFound();

        // The same UTC month the panel's card is about, from the same clock — so a script and the
        // screen beside it never disagree about what «this month» means.
        var spent = await traffic.MonthAsync(
            tenantId,
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
            cancellationToken);

        // The plan's name is deliberately absent. What a program can act on is the numbers; the tier
        // it is on is a commercial fact that belongs on a screen a person reads.
        return Ok(new V1Usage(
            plan.StorageUsedBytes,
            plan.Limits.StorageBytes,
            plan.Limits.MaxFileBytes,
            spent.EgressBytes,
            plan.Limits.MonthlyEgressBytes,
            spent.Downloads));
    }
}
