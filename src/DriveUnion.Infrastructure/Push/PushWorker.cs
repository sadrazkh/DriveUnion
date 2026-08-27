using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// Drives <see cref="IPushDispatcher"/>, and sweeps the devices nothing has heard from.
///
/// <para>Every decision worth arguing about — which devices an event is for, what a status code
/// means, when a row dies — is in <see cref="PushDispatcher"/> and <see cref="PushSubscriptionStore"/>,
/// where a test can call it and watch what happens. This is the loop, and a loop is the one thing a
/// test cannot usefully drive.</para>
///
/// <para>Registered separately from <see cref="PushServiceCollectionExtensions.AddDriveUnionPush"/>,
/// like the deletion worker and the trash sweeper and for the same reason: every in-process test
/// host boots the pipeline over one shared SQLite connection, and a background loop opening scopes
/// against it turns unrelated suites into «database is locked».</para>
/// </summary>
public sealed class PushWorker(
    PushOutbox outbox,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<PushWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often the devices nothing has heard from are swept.
    ///
    /// <para>Six hours. The rows it removes have been dead for ninety days — see
    /// <c>PushSubscription.StaleAfter</c> — so nothing is waiting on this, and a longer interval
    /// only means a deployment that is restarted daily never runs it at all.</para>
    /// </summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Started at the epoch so the first event through the door also triggers the first sweep.
        // A deployment that is busy never sweeps otherwise, because the interval would be measured
        // from a start-up that keeps happening.
        var lastSwept = DateTimeOffset.MinValue;

        try
        {
            await foreach (var notification in outbox.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await DeliverAsync(notification, stoppingToken).ConfigureAwait(false);

                if (clock.GetUtcNow() - lastSwept < SweepEvery) continue;

                lastSwept = clock.GetUtcNow();

                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping, which is the only way out of the loop above.
        }
    }

    private async Task DeliverAsync(PushEvent notification, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IPushDispatcher>();

            await dispatcher.DeliverAsync(notification, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The loop outlives one bad event. A device that cannot be reached records its own
            // failure on its own row; what reaches here is something wider — a database that went
            // away, a scope that could not be built — and the answer to all of those is to carry on
            // rather than to stop notifying for the life of the process.
            logger.LogError(exception, "Delivering a {Kind} notification failed.", notification.Kind);
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IPushSubscriptions>();

            var swept = await store.SweepStaleAsync(stoppingToken).ConfigureAwait(false);

            if (swept > 0) logger.LogInformation("Removed {Count} stale push subscription(s).", swept);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Sweeping stale push subscriptions failed.");
        }
    }
}

/// <summary>The two lines that turn notifications on.</summary>
public static class PushServiceCollectionExtensions
{
    /// <summary>
    /// The store, the encryption, the sender and the dispatcher.
    ///
    /// <para>Not <see cref="IPushEvents"/>, which is registered by <c>AddDriveUnionServices</c>
    /// alongside the code that raises events. That split is deliberate: the doorbell costs nothing
    /// and depends on nothing, so a host that has the application layer can always raise; delivering
    /// needs the network and the operator's keys, and a host that has neither should be able to say
    /// so by leaving this line out.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionPush(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPushSubscriptions, PushSubscriptionStore>();
        services.TryAddScoped<IPushDispatcher, PushDispatcher>();
        services.TryAddSingleton<IWebPushSender, WebPushSender>();

        services.AddHttpClient(WebPushSender.ClientName, client =>
        {
            // A push service that has stopped answering must not hold this worker for a minute and a
            // half, which is HttpClient's default. Ten seconds is generous for a request whose body
            // is two hundred bytes, and the answer to a slow one is the failure counter — five slow
            // afternoons in a row is an endpoint worth forgetting.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }

    /// <summary>
    /// Adds the loop that actually sends.
    ///
    /// <para>Separate for the reason on <see cref="PushWorker"/>. Without this line the panel still
    /// offers the control, still stores the subscription, and still raises every event — and not one
    /// notification is ever delivered. That is the quiet version of the bug this pair exists to
    /// prevent, and it is quiet precisely because nothing a customer can see depends on it.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionPushWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<PushWorker>();

        return services;
    }
}
