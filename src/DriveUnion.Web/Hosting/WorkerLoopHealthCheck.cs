using DriveUnion.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Has every background loop that has ever reported in turned within <c>window</c>?
///
/// <para>The loops are the half of this process nothing observes. A faulted <c>ExecuteAsync</c>
/// leaves a panel that serves every page perfectly while files are never purged, locks are never
/// sealed and the catalogue is never backed up — and the first person to notice is a customer, weeks
/// later. <see cref="WorkerHeartbeat"/> is where the loops say they are alive; this is the only
/// thing that reads it.</para>
///
/// <para><b>Why the silences arrive through a delegate.</b> <see cref="WorkerHeartbeat"/> is static
/// — see its own remarks for why that trade is the right one — and a static thing with a clock in it
/// is a thing no test can drive. Keeping the reading here and the policy in this class means the
/// threshold, which is the part worth arguing about, is the part a test can construct.</para>
///
/// <para><b>Every loop, not the busiest one.</b> The alternative considered was «at least one loop
/// has turned», which cannot flap and also cannot see a single dead loop, which is the failure this
/// exists for. Reporting on all of them is only safe because a loop inside a long pass reports zero
/// silence rather than growing quiet — without that, a deployment doing real work would go red for
/// doing it.</para>
/// </summary>
internal sealed class WorkerLoopHealthCheck(
    Func<IReadOnlyList<WorkerSilence>> silences,
    TimeSpan window) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var stalled = silences().Any(loop => loop.Since > window);

        // Which loop stopped is not in the answer. It would be useful and it is not this endpoint's
        // to give: /readyz is anonymous, and the names of a deployment's background jobs are a map
        // of what it does. The operator's log has the loop that stopped logging; this says only that
        // something did.
        return Task.FromResult(stalled ? HealthCheckResult.Unhealthy() : HealthCheckResult.Healthy());
    }
}
