using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Can this process open a connection to its own database?
///
/// <para>Deliberately <see cref="RelationalDatabaseFacadeExtensions"/>'s cheapest question and not a
/// query. A readiness probe is answered every few seconds for the life of the deployment, so it must
/// cost a pooled connection and nothing else — no <c>SELECT</c> over a table whose size nobody is
/// watching, and no write.</para>
///
/// <para><b>Nothing it learns is put in the result.</b> Not the host, not the database name, not the
/// driver's message. <c>CanConnectAsync</c> already swallows the provider's exception and answers
/// false; the <c>catch</c> is for what it cannot swallow — a context whose options could not be
/// built, a connection disposed under it — and it discards the exception rather than describing it,
/// because the caller of this endpoint is anonymous and a Npgsql failure message carries the host,
/// the port and the user it tried.</para>
/// </summary>
internal sealed class DatabaseReachableHealthCheck(DriveUnionDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A shutdown mid-probe is not an unhealthy database, which is why the filter is here
            // rather than a bare catch.
            return HealthCheckResult.Unhealthy();
        }
    }
}
