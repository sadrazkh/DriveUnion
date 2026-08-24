using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.EntityFrameworkCore;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The drainer: two loops, because a chat reply must never queue behind a two-gigabyte transfer.
///
/// <para>The short loop carries text, cards, callbacks and deletions — everything measured in
/// milliseconds. The transfer loop carries the two byte-moving kinds and runs at most
/// <c>Telegram:MaxConcurrentTransfers</c> at a time. Without the split, twenty queued deliveries
/// saturate the uplink, the disk and every worker at once, and the replies that would explain what is
/// happening are stuck behind the transfers causing it.</para>
///
/// <para>Everything it does is in <see cref="TelegramOutboxProcessor"/>, which a test constructs
/// directly. This class is the loop and the scope, and there is deliberately nothing else in it: a
/// long-running background thing that quietly does nothing is the failure mode the rest of the product
/// keeps designing against, and the way to keep it out of here is to keep the decisions out of here.
/// </para>
/// </summary>
public sealed class TelegramOutboxDrainer(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    TimeProvider clock,
    ILogger<TelegramOutboxDrainer> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idle = TimeSpan.FromSeconds(Math.Max(1, _options.DrainIntervalSeconds));

        var shortLoop = LoopAsync(movesBytes: false, 1, idle, stoppingToken);
        var transferLoop = LoopAsync(
            movesBytes: true,
            Math.Max(1, _options.MaxConcurrentTransfers),
            idle,
            stoppingToken);

        await Task.WhenAll(shortLoop, transferLoop);
    }

    private async Task LoopAsync(
        bool movesBytes,
        int concurrency,
        TimeSpan idle,
        CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(concurrency, concurrency);
        var running = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(stoppingToken);

                var item = await ClaimAsync(movesBytes, stoppingToken);

                if (item is null)
                {
                    slots.Release();
                    await Task.Delay(idle, clock, stoppingToken);
                    continue;
                }

                running.RemoveAll(t => t.IsCompleted);
                running.Add(RunAsync(item, slots, stoppingToken));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything one item can do to it. A drainer that dies on a bad
                // row is a bot that goes quiet with nothing in any log to say why.
                logger.LogError(ex, "The Telegram outbox drainer hit an error and will continue.");

                try
                {
                    await Task.Delay(idle, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Shutdown waits for what is in flight, and a cancellation here is the shutdown rather than a
        // fault: letting it out would make an ordinary stop look like a crashed background service.
        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<TelegramOutbox?> ClaimAsync(bool movesBytes, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<TelegramOutboxProcessor>()
            .ClaimNextAsync(movesBytes, stoppingToken);
    }

    private async Task RunAsync(TelegramOutbox item, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            // A scope of its own, because a transfer holds one for minutes and a request-scoped
            // DbContext shared with the claim loop would be used by two operations at once.
            await using var scope = scopes.CreateAsyncScope();

            await scope.ServiceProvider
                .GetRequiredService<TelegramOutboxProcessor>()
                .ExecuteAsync(item, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping mid-item. The row stays Claimed and the sweeper hands it back to
            // the queue once it is plainly stale — see TelegramSweeperService.RecoverStaleClaims.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A Telegram outbox item of kind {Kind} could not be carried out.", item.Kind);
        }
        finally
        {
            slots.Release();
        }
    }
}

/// <summary>
/// Long polling, which is what development runs.
///
/// <para>It exists because this machine has no public HTTPS and no local Bot API server, so without it
/// the bot cannot be exercised at all on the machine the product is developed on — the same constraint
/// that put the Drive client behind an interface in the first place. Production uses the webhook, and
/// switching is one setting.</para>
///
/// <para><b>A poller must be a singleton across the deployment.</b> Two instances calling
/// <c>getUpdates</c> produce <c>409 Conflict: terminated by other getUpdates request</c>, and the
/// symptom is intermittently missing messages — invisible in development and maddening in production.
/// Nothing here enforces that, because the setting that turns it on is the setting that says this is a
/// single-process development box; a lease is the deployed answer and the deployed answer is the
/// webhook.</para>
/// </summary>
public sealed class TelegramPollingService(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    TimeProvider clock,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.UpdateSource is not TelegramUpdateSource.Polling) return;

        var offset = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();

                var telegram = scope.ServiceProvider.GetRequiredService<ITelegramBotGateway>();
                var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();

                // Nothing is asked of Telegram until an operator has connected a bot. Polling is the
                // default update source, so without this an unconfigured deployment — which is every
                // deployment on its first day, and every in-process test host — opens a long-poll
                // against Telegram for a bot that does not exist.
                //
                // Re-read each round rather than checked once: the bot arrives while this is
                // running, and a poller that decided at startup would need a restart to notice.
                var settings = scope.ServiceProvider.GetRequiredService<ITelegramBotSettingsStore>();

                if (await settings.ReadBotTokenAsync(stoppingToken) is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
                    continue;
                }

                var updates = await telegram.GetUpdatesAsync(
                    offset,
                    _options.PollTimeoutSeconds,
                    stoppingToken);

                if (!updates.Ok)
                {
                    // Including "no token configured", which is the ordinary state of a fresh
                    // deployment and must not become a log the operator has to scroll past.
                    logger.LogDebug("getUpdates did not answer: {Description}", updates.Failure.Description);

                    await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
                    continue;
                }

                foreach (var update in updates.Value)
                {
                    // The offset advances whether or not the update was acted on. An update that is
                    // refused — a stranger past their budget, a group chat — is still consumed, or
                    // Telegram hands it back for ever.
                    offset = Math.Max(offset, update.UpdateId + 1);

                    await handler.HandleAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The Telegram poller hit an error and will continue.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Every minute, the working directory and the two tables that grow without bound.
///
/// <para>A <see cref="BackgroundService"/> rather than a cron entry or a <c>find -delete</c> in the
/// service unit, for one reason: <b>a shell one-liner has no test</b>. And every minute rather than
/// nightly, because a nightly sweep of a directory that can gain two gigabytes per message is not a
/// sweep.</para>
/// </summary>
public sealed class TelegramSweeperService(
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    TimeProvider clock,
    ILogger<TelegramSweeperService> logger) : BackgroundService
{
    private readonly TelegramOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.SweepIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var provider = scope.ServiceProvider;

                var botUserId = (await provider
                        .GetRequiredService<ITelegramBotSettingsStore>()
                        .ReadAsync(stoppingToken))
                    .BotUserId;

                var swept = provider.GetRequiredService<TelegramWorkDirectory>().Sweep(botUserId);

                if (swept.FilesDeleted > 0 || swept.BytesRemaining > 0)
                {
                    // Deletions here are the crash path, so a non-zero count is worth a line. What is
                    // worth more is the remaining size: in a healthy production it is zero, because
                    // delete-on-success does the normal work, and a non-zero size sustained across
                    // several minutes means that has stopped.
                    logger.LogWarning(
                        "The Telegram working directory swept {Deleted} file(s) and still holds "
                        + "{RemainingFiles} file(s) of {RemainingBytes} bytes.",
                        swept.FilesDeleted,
                        swept.FilesRemaining,
                        swept.BytesRemaining);
                }

                await provider.GetRequiredService<ITelegramUpdateLedger>().SweepAsync(stoppingToken);

                var db = provider.GetRequiredService<DriveUnionDbContext>();

                await RecoverStaleClaimsAsync(db, stoppingToken);
                await SweepOutboxAsync(db, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The Telegram sweeper hit an error and will continue.");
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

    /// <summary>
    /// How long a claim may sit before it is treated as abandoned. Generous on purpose: a
    /// ceiling-sized transfer on a slow uplink legitimately holds one for the better part of an hour,
    /// and handing a live item back to the queue would send the same file twice.
    /// </summary>
    public static readonly TimeSpan StaleClaim = TimeSpan.FromHours(2);

    /// <summary>
    /// Items a process died holding.
    ///
    /// <para>Without this a deploy landing mid-transfer leaves a row <c>Claimed</c> for ever: nothing
    /// retries it, nothing reports it, and the customer's file simply never arrives. It is the
    /// counterpart of the drainer's claim being a conditional update — the claim is what stops two
    /// workers taking one item, and this is what stops one worker taking it to the grave.</para>
    /// </summary>
    public static async Task<int> RecoverStaleClaimsAsync(
        DriveUnionDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var cutoff = clock.GetUtcNow() - StaleClaim;

        // Judged here: SQLite will not compare a DateTimeOffset in SQL.
        var claimed = await db.TelegramOutbox
            .AsNoTracking()
            .Where(o => o.Status == TelegramOutboxStatus.Claimed)
            .Select(o => new { o.Id, o.ClaimedAt })
            .ToListAsync(cancellationToken);

        var stale = claimed
            .Where(o => o.ClaimedAt is null || o.ClaimedAt < cutoff)
            .Select(o => o.Id)
            .ToList();

        if (stale.Count == 0) return 0;

        return await db.TelegramOutbox
            .Where(o => stale.Contains(o.Id))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, TelegramOutboxStatus.Pending)
                    .SetProperty(o => o.ClaimedAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    private Task<int> RecoverStaleClaimsAsync(DriveUnionDbContext db, CancellationToken cancellationToken) =>
        RecoverStaleClaimsAsync(db, clock, cancellationToken);

    /// <summary>
    /// Finished outbox rows past their usefulness. They are kept for a week rather than deleted on
    /// success because <c>SentMessageId</c> is what a deletion names, and a delivery whose lifetime is
    /// armed for tomorrow needs the row that recorded it.
    /// </summary>
    private async Task<int> SweepOutboxAsync(DriveUnionDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(7);

        // Dates judged here: SQLite will not compare a DateTimeOffset in SQL.
        var finished = await db.TelegramOutbox
            .AsNoTracking()
            .Where(o => o.Status == TelegramOutboxStatus.Sent || o.Status == TelegramOutboxStatus.Failed)
            .Select(o => new { o.Id, o.CreatedAt })
            .ToListAsync(cancellationToken);

        var doomed = finished.Where(o => o.CreatedAt < cutoff).Select(o => o.Id).ToList();

        if (doomed.Count == 0) return 0;

        return await db.TelegramOutbox
            .Where(o => doomed.Contains(o.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
