using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// Drives <see cref="IDeletionRunner"/>, and nothing else.
///
/// <para>Every decision worth arguing about — which job is next, which file it still owes, what a
/// refusal costs and what a rate limit does — is in the runner, where a test can call it and watch
/// what happens. This is the loop, and a loop is the one thing a test cannot usefully drive.</para>
///
/// <para>Registered separately from <see cref="DeletionServiceCollectionExtensions.AddDriveUnionDeletions"/>,
/// like the trash sweeper and the migration worker and for the same reason: every in-process test
/// host boots the pipeline over one shared SQLite connection, and a background loop opening scopes
/// against it turns unrelated suites into «database is locked».</para>
/// </summary>
public sealed class DeletionWorker(
    IServiceScopeFactory scopes,
    ILogger<DeletionWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a pass that moved nothing.
    ///
    /// <para>Ten seconds. Nobody is watching this — the delete the customer asked for finished in
    /// their request — so the only cost of waiting is a file sitting in the wrong folder inside the
    /// operator's own Drive for a few seconds longer. A pass that <i>did</i> move something comes
    /// straight back round, so a real pile drains at the speed of Google rather than of this
    /// timer.</para>
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Files per pass.
    ///
    /// <para>Fifty, which is about fifteen seconds of Drive round trips. A bound rather than «until
    /// the job is done», so one folder of forty thousand files cannot hold a scope — and a database
    /// connection — open for hours, and so the Drive request budget this spends is handed back to
    /// the customers uploading with it at regular intervals.</para>
    /// </summary>
    private const int FilesPerPass = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var moved = 0;

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<IDeletionRunner>();

                moved = await runner.RunOnceAsync(FilesPerPass, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The loop outlives one bad pass. A file that cannot be moved records its own
                // failure on its own row; what reaches here is something wider — a database that
                // went away, a scope that could not be built — and the answer to all of those is to
                // wait and try again rather than to stop tidying for the life of the process.
                logger.LogError(exception, "A deletion pass failed.");
                moved = 0;
            }

            if (moved == 0)
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

/// <summary>The two lines that turn deleting a lot at once on.</summary>
public static class DeletionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue a screen presses and the runner that owes it Drive moves.
    ///
    /// <para>After <c>AddDriveUnionTrash</c>, whose <see cref="ITrashMover"/> and operator settings
    /// row both halves read: the retention window is stamped when the customer presses delete, and
    /// the move goes to the same trash folder a single-file delete uses.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionDeletions(this IServiceCollection services)
    {
        services.TryAddScoped<IDeletionQueue, DeletionQueue>();
        services.TryAddScoped<IDeletionRunner, DeletionRunner>();

        return services;
    }

    /// <summary>
    /// Adds the loop that actually moves the files.
    ///
    /// <para>Separate for the reason on <see cref="DeletionWorker"/>. Without this line in
    /// <c>Program.cs</c> a delete still deletes — the customer sees exactly what they expect — and
    /// the files sit for ever in the folder they were uploaded to until the purge destroys them.
    /// That is the quiet version of the bug this exists to prevent, and it is quiet precisely
    /// because nothing a customer can see depends on it.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionDeletionWorker(this IServiceCollection services)
    {
        services.AddHostedService<DeletionWorker>();

        return services;
    }
}
