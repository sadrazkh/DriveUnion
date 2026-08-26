using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Drives <see cref="IRemoteFetcher"/>, and nothing else.
///
/// <para>Every decision worth arguing about is in the fetcher, where a test can call it. This is the
/// loop, and a loop is the one thing a test cannot usefully drive.</para>
///
/// <para>Registered separately from <c>AddDriveUnionInfrastructure</c>, like the trash sweeper and
/// the migration worker and for the same reason: every in-process test host boots the pipeline over
/// one shared SQLite connection, and a background loop opening scopes against it turns unrelated
/// suites into «database is locked».</para>
/// </summary>
public sealed class RemoteFetchWorker(
    IServiceScopeFactory scopes,
    ILogger<RemoteFetchWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Fetches per pass.
    ///
    /// <para>One. A pull is a whole file down somebody else's connection and back up to storage, and
    /// the operator's line is the thing being shared — running four at once makes each of them a
    /// quarter as fast and none of them finish sooner.</para>
    /// </summary>
    private const int FetchesPerPass = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var done = 0;

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var fetcher = scope.ServiceProvider.GetRequiredService<IRemoteFetcher>();

                done = await fetcher.RunOnceAsync(FetchesPerPass, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The loop outlives one bad pass. A fetch that cannot make progress records its own
                // failure on its own row; what reaches here is something wider, and the answer to
                // all of those is to wait and try again rather than to stop fetching for the life of
                // the process.
                logger.LogError(exception, "A remote fetch pass failed.");
                done = 0;
            }

            if (done == 0)
            {
                try
                {
                    await Task.Delay(Idle, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>The lines that turn fetching on.</summary>
public static class RemoteFetchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue, the fetcher, and the one HTTP client either is allowed to use.
    ///
    /// <para>The client is named and its handler is <see cref="GuardedFetchHandler"/>'s. Nothing
    /// else in the product may fetch a customer-supplied URL, and this is the registration that
    /// makes «the guarded one» the only one there is.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionRemoteFetch(this IServiceCollection services)
    {
        // Singleton: the key for an encrypted fetch is put here by a web request and read by a
        // worker minutes later, so it has to outlive both scopes — and live nowhere else. See
        // FetchKeyring for what a restart costs and why that is the right price.
        services.AddSingleton<FetchKeyring>();

        services.AddScoped<IRemoteFetches, RemoteFetches>();
        services.AddScoped<IRemoteFetcher, RemoteFetcher>();

        services
            .AddHttpClient(RemoteFetcher.ClientName)
            .ConfigurePrimaryHttpMessageHandler(GuardedFetchHandler.Create)

            // No overall timeout on the client: the fetcher sets its own deadline for the whole
            // transfer, and HttpClient's would cut a legitimate 40 GB pull at a hundred seconds.
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        return services;
    }

    /// <summary>
    /// Adds the loop that actually pulls files.
    ///
    /// <para>Separate for the reason on <see cref="RemoteFetchWorker"/>. Without this line in
    /// <c>Program.cs</c> a link is accepted, shown as queued, and never fetched — the quiet version
    /// of the bug this exists to prevent.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionRemoteFetchWorker(this IServiceCollection services)
    {
        services.AddHostedService<RemoteFetchWorker>();

        return services;
    }
}
