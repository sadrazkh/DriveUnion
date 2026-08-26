using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.S3;

/// <summary>
/// Takes the multipart uploads nobody finished.
///
/// <para>A client that dies between its first part and its completion leaves bytes on the operator's
/// volume that nothing will ever ask for — and precisely because it died, nothing arrives to trigger
/// a cleanup. S3 answers this with lifecycle rules the customer configures; this is the version that
/// needs no configuring.</para>
///
/// <para>Registered separately from the gateway, like the trash sweeper and the Telegram drainer and
/// for the same reason: every in-process test host boots this pipeline over one shared SQLite
/// connection, and a background loop opening scopes against it turns unrelated suites into «database
/// is locked». <c>AddDriveUnionS3</c> is what a test calls; this line is what production adds.</para>
/// </summary>
public sealed class S3StagingSweeper(
    IServiceScopeFactory scopes,
    S3StagingDirectory staging,
    ILogger<S3StagingSweeper> logger) : BackgroundService
{
    /// <summary>
    /// How often to look.
    ///
    /// <para>An hour against a day's grace: the thing being reclaimed is disk, the deadline is not
    /// sharp, and a sweep that ran every minute would be a query per minute for the whole life of
    /// the process to find nothing almost every time.</para>
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!staging.IsConfigured)
        {
            // Nothing stages here, so there is nothing to sweep. Said once at start-up rather than
            // looping quietly for ever, so an operator who expected multipart to work finds out.
            logger.LogInformation(
                "No S3 staging directory is configured, so multipart upload is off and nothing is swept.");

            return;
        }

        // The first pass waits, like the trash sweeper's: a host that boots and immediately opens a
        // scope against a shared connection is the shape that made unrelated suites flaky.
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var multipart = scope.ServiceProvider.GetRequiredService<IS3Multipart>();

                await multipart.SweepAbandonedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A sweep that throws must not end the loop: what it is reclaiming is disk, and a
                // dead sweeper fills a volume silently over days.
                logger.LogError(exception, "Sweeping abandoned S3 multipart uploads failed.");
            }
        }
    }
}

public static class S3ServiceCollectionExtensions
{
    /// <summary>
    /// The staging sweeper.
    ///
    /// <para>Separate from the gateway's own registrations for the reason on
    /// <see cref="S3StagingSweeper"/>: a test host must be able to have the gateway without the
    /// loop. Without this line in <c>Program.cs</c> nothing is ever reclaimed and an abandoned
    /// upload's parts sit on the volume until somebody notices, which is the quiet version of the
    /// bug this exists to prevent.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionS3Sweeper(this IServiceCollection services)
    {
        services.AddHostedService<S3StagingSweeper>();

        return services;
    }
}
