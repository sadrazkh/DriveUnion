using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// The sweeper's loop, and deliberately nothing else.
///
/// <para>Every decision it could have made is in <see cref="TrashPurge"/>, which a test constructs
/// directly — the same split <c>TelegramSweeperService</c> uses, and for the same reason: a
/// long-running background thing that quietly does nothing is the failure mode this product keeps
/// designing against, and the way to keep it out is to keep the decisions out.</para>
///
/// <para>A <see cref="BackgroundService"/> rather than a cron entry, because a shell one-liner has
/// no test. The bound on how much it does per pass is <c>Trash:PurgeBatchSize</c>, and it exists so
/// that housekeeping cannot spend the Drive request budget the customers are uploading with.</para>
/// </summary>
public sealed class TrashPurgeService(
    IServiceScopeFactory scopes,
    IOptions<TrashOptions> options,
    TimeProvider clock,
    ILogger<TrashPurgeService> logger) : BackgroundService
{
    private readonly TrashOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Floors rather than validation at start-up: a mistyped zero here must not be a panel that
        // refuses to boot, and it must not be a loop that spins either.
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.PurgeIntervalSeconds));
        var batchSize = Math.Max(1, _options.PurgeBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A scope per pass, because the purge holds a DbContext for as long as it takes to
                // delete a batch in Drive and nothing else should be sharing it.
                await using var scope = scopes.CreateAsyncScope();

                var started = clock.GetTimestamp();

                var purged = await scope.ServiceProvider
                    .GetRequiredService<ITrashPurge>()
                    .PurgeDueAsync(batchSize, stoppingToken);

                if (purged > 0)
                {
                    // Only when something happened. A line every five minutes saying nothing was due
                    // is a log an operator learns to scroll past, and the one line that mattered
                    // would be in the middle of it.
                    logger.LogInformation(
                        "The trash sweeper destroyed {Purged} file(s) in {ElapsedMs} ms.",
                        purged,
                        (long)clock.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything one pass can do to it. A sweeper that dies on a bad
                // row is a pool that fills up with nothing in any log to say why.
                logger.LogError(ex, "The trash sweeper hit an error and will continue.");
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
