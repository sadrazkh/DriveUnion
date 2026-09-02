using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Drives <see cref="IFileLockRunner"/>, and nothing else.
///
/// <para>Every decision worth arguing about — the order the copies are made and destroyed in, what a
/// failed checksum costs, what happens to a job whose swap landed and whose delete did not — is in
/// the runner, where a test can call it and watch. This is the loop, which is the one thing a test
/// cannot usefully drive.</para>
///
/// <para>Registered separately from <see cref="FileLockServiceCollectionExtensions.AddDriveUnionFileLocks"/>,
/// like every other worker here and for the same reason: every in-process test host boots the
/// pipeline over one shared SQLite connection, and a background loop opening scopes against it turns
/// unrelated suites into «database is locked».</para>
/// </summary>
public sealed class FileLockWorker(
    IServiceScopeFactory scopes,
    ILogger<FileLockWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a pass that locked nothing.
    ///
    /// <para>Five seconds, which is shorter than the deletion worker's ten. Somebody <i>is</i>
    /// watching this one: they typed a passphrase and are looking at a row that says the file is
    /// being locked, and the difference between five seconds and ten is the difference between a
    /// screen that responds and one that seems stuck.</para>
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Files per pass.
    ///
    /// <para>Two. Every one of them reads a whole file out of Drive and writes a whole encrypted
    /// copy back, so this is the most expensive work in the product per job — a bound of fifty
    /// would hold a scope, and a database connection, for as long as fifty files take to copy
    /// twice. Two keeps the pass short and hands the Drive budget back to the customers uploading
    /// with it.</para>
    /// </summary>
    private const int LocksPerPass = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var done = 0;

            try
            {
                using var beat = WorkerHeartbeat.Beat(nameof(FileLockWorker));
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<IFileLockRunner>();

                done = await runner.RunOnceAsync(LocksPerPass, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The loop outlives one bad pass. A file that cannot be sealed records its own
                // failure on its own row; what reaches here is something wider — a database that
                // went away, a scope that could not be built — and the answer to all of those is to
                // wait and try again rather than to stop locking for the life of the process.
                logger.LogError(exception, "A file-locking pass failed.");
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

/// <summary>The two lines that turn locking an already-uploaded file on.</summary>
public static class FileLockServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue a screen presses and the runner that owes it the work.
    ///
    /// <para><c>ContentKeyring</c> is a singleton and is shared with the link-upload path: both hold
    /// a content key in memory for the length of one job and neither writes one down. The ids are
    /// Guids from different tables, so one dictionary serves both without them meeting.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionFileLocks(this IServiceCollection services)
    {
        services.TryAddScoped<IFileLocks, FileLocks>();
        services.TryAddScoped<IFileLockRunner, FileLocker>();

        return services;
    }

    /// <summary>
    /// Adds the loop that actually does it.
    ///
    /// <para>Separate for the reason on <see cref="FileLockWorker"/>. Without this line in
    /// <c>Program.cs</c> the button still works and the row still appears — the customer sees
    /// exactly what they expect — and the file is never locked, for ever, with the room for its
    /// second copy reserved and never given back. That is the quiet version of this bug, and it is
    /// quiet precisely because the half a customer can see is the half that does not need the
    /// loop.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionFileLockWorker(this IServiceCollection services)
    {
        services.AddHostedService<FileLockWorker>();

        return services;
    }
}
