using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Backup;

/// <summary>
/// Drives <see cref="ICatalogueBackup"/>, and nothing else.
///
/// <para>Every decision worth arguing about — what goes in a snapshot, which accounts get a copy,
/// what counts as a copy that landed, how many are kept — is in <see cref="CatalogueBackup"/>, where
/// a test can call it and read the bytes that came out. This is the loop, and a loop is the one
/// thing a test cannot usefully drive.</para>
///
/// <para>Registered separately from <see cref="CatalogueBackupServiceCollectionExtensions.AddDriveUnionCatalogueBackup"/>,
/// like the trash sweeper and the migration worker and for the same reason: every in-process test
/// host boots the pipeline over one shared SQLite connection, and a background loop opening scopes
/// against it turns unrelated suites into «database is locked».</para>
/// </summary>
public sealed class CatalogueBackupWorker(
    IServiceScopeFactory scopes,
    ILogger<CatalogueBackupWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait between passes.
    ///
    /// <para>A minute, for a job that runs once a day. The pass that finds nothing due is two small
    /// indexed queries, and the reason it is not an hour is the other caller: an operator who has
    /// pressed «take one now» is standing in front of the screen waiting for it, usually because
    /// they are about to do something they might have to undo.</para>
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var backup = scope.ServiceProvider.GetRequiredService<ICatalogueBackup>();

                var copies = await backup.RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // Only after something was written. Pruning is what makes room for the snapshot that
                // has just arrived, so it belongs immediately after one — and a prune on every idle
                // pass would be a query a minute, for ever, to find the same nothing.
                if (copies > 0) await backup.PruneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The loop outlives one bad pass. A snapshot that cannot be written records its own
                // failure on its own row, where the operator's screen shows it; what reaches here is
                // something wider — a database that went away, a scope that could not be built — and
                // the answer to all of those is to wait and try again rather than to stop backing up
                // the catalogue for the life of the process.
                logger.LogError(exception, "A catalogue backup pass failed.");
            }

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

/// <summary>The lines that turn catalogue backups on.</summary>
public static class CatalogueBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the snapshot writer and the operator's view of it.
    ///
    /// <para>Both scoped, because both share the request's — or the pass's —
    /// <c>DriveUnionDbContext</c>. This line alone gives a host the screen and the ability to take a
    /// snapshot; it deliberately does not start anything.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionCatalogueBackup(this IServiceCollection services)
    {
        services.TryAddScoped<ICatalogueSnapshots, CatalogueSnapshots>();
        services.TryAddScoped<ICatalogueBackup, CatalogueBackup>();

        return services;
    }

    /// <summary>
    /// Adds the loop that actually writes them.
    ///
    /// <para>Separate for the reason on <see cref="CatalogueBackupWorker"/>. Without this line in
    /// <c>Program.cs</c> the screen exists, the button queues a row, and no snapshot is ever
    /// written — which is the quiet version of the bug this whole slice exists to prevent, and the
    /// one nobody notices until the database is gone.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionCatalogueBackupWorker(this IServiceCollection services)
    {
        services.AddHostedService<CatalogueBackupWorker>();

        return services;
    }
}
