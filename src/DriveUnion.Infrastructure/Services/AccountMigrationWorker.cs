using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// Drives <see cref="IAccountMigrator"/>, and nothing else.
///
/// <para>Every decision worth arguing about — which file is next, what counts as a verified copy,
/// when a source may be deleted — is in the migrator, where a test can call it and watch what
/// happens. This is the loop, and a loop is the one thing a test cannot usefully drive.</para>
///
/// <para>Registered separately from <c>AddDriveUnionInfrastructure</c>, like the trash sweeper and
/// the Telegram drainer and for the same reason: every in-process test host boots the pipeline over
/// one shared SQLite connection, and a background loop opening scopes against it turns unrelated
/// suites into «database is locked».</para>
/// </summary>
public sealed class AccountMigrationWorker(
    IServiceScopeFactory scopes,
    ILogger<AccountMigrationWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a pass that moved nothing.
    ///
    /// <para>Thirty seconds, because there is usually no migration at all: an operator drains an
    /// account a handful of times in the life of a deployment. A pass that <i>did</i> move something
    /// comes straight back round, so an active drain runs at the speed of Google rather than at the
    /// speed of this timer.</para>
    /// </summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Files per pass.
    ///
    /// <para>A bound rather than «until it is done», so one drain of forty thousand files cannot hold
    /// a scope — and a database connection — open for a day. It also puts a ceiling on how much of
    /// the operator's outbound bandwidth this can occupy before it lets go and looks around.</para>
    /// </summary>
    private const int FilesPerPass = 20;

    /// <summary>
    /// How often the sources left behind are swept.
    ///
    /// <para>Every twentieth pass — roughly ten minutes while idle. The thing being reclaimed is
    /// space in the operator's pool against a six-hour grace period, so the deadline is not sharp
    /// and a query per pass would be a query per pass to find nothing.</para>
    /// </summary>
    private const int SweepEvery = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pass = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var moved = 0;

            try
            {
                using var beat = WorkerHeartbeat.Beat(nameof(AccountMigrationWorker));
                await using var scope = scopes.CreateAsyncScope();
                var migrator = scope.ServiceProvider.GetRequiredService<IAccountMigrator>();

                moved = await migrator.RunOnceAsync(FilesPerPass, stoppingToken).ConfigureAwait(false);

                if (pass++ % SweepEvery == 0)
                {
                    await migrator.SweepMovedSourcesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The loop outlives one bad pass. A migration that cannot make progress records its
                // own failures per file; what reaches here is something wider — a database that went
                // away, a scope that could not be built — and the answer to all of those is to wait
                // and try again rather than to stop moving files for the life of the process.
                logger.LogError(exception, "An account migration pass failed.");
                moved = 0;
            }

            // Straight back round while it is making progress; a pause when there is nothing to do.
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

/// <summary>
/// The one line that turns migrations on in production.
/// </summary>
public static class AccountMigrationWorkerExtensions
{
    /// <summary>
    /// Adds the loop that drains accounts.
    ///
    /// <para>Separate from <c>AddDriveUnionInfrastructure</c> for the reason on
    /// <see cref="AccountMigrationWorker"/>: a test host must be able to have the migrator without
    /// the loop. Without this line in <c>Program.cs</c> a drain is accepted, shown as pending, and
    /// never moves a file — which is the quiet version of the bug this exists to prevent.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionAccountMigrations(this IServiceCollection services)
    {
        services.AddHostedService<AccountMigrationWorker>();

        return services;
    }
}
