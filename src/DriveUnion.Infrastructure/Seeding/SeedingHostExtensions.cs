using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Infrastructure.Seeding;

public static class SeedingHostExtensions
{
    /// <summary>
    /// Runs the seeder once, before the first request is served.
    ///
    /// A call at the composition root rather than an <c>IHostedService</c>, so that "this deployment
    /// creates accounts at boot" is a visible line in Program.cs instead of a side effect of a
    /// registration. It does nothing — and touches no database — when nothing is configured.
    /// </summary>
    public static async Task SeedDriveUnionAsync(
        this IHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IdentitySeeder>()
            .SeedAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
