using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// Says, at every boot, that this process is not talking to Google.
///
/// A file host cannot look different from the outside depending on where the bytes are, so the log
/// line is the only place anybody finds out. It is a warning rather than information on purpose:
/// whoever is reading the log of a box that is serving customers' files off its own disk needs to
/// see it without looking for it.
///
/// It also takes the client as a dependency, which constructs it — a root path that cannot be
/// created fails the boot here rather than the first upload an hour later.
///
/// Public rather than internal so the refusal below can be exercised on its own. Reached through the
/// container in <c>Production</c>, options validation stops the backend before this type is even
/// constructed; the check here is what is left when that validation has been switched off.
/// </summary>
public sealed class LocalDiskDriveAnnouncement(
    LocalDiskDriveClient client,
    IHostEnvironment environment,
    ILogger<LocalDiskDriveAnnouncement> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The options validator already refuses this at start-up. Repeated here because that check
        // can be switched off by a configuration change and this one cannot: nothing serves a byte
        // from this backend until this method has returned.
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{LocalDiskDriveOptions.SectionName}:Enabled is true in the Production environment. This "
                + "backend stores customers' files on this box's disk instead of the Google account "
                + "pool. It refuses to start.");
        }

        logger.LogWarning(
            "Drive Union is serving files from LOCAL DISK at {RootPath} in the {EnvironmentName} "
            + "environment. Nothing is being stored in Google Drive: these bytes live on this one "
            + "machine, are not replicated and are not backed up. This backend exists so the product "
            + "can be seen working without a Google Cloud project.",
            client.RootPath,
            environment.EnvironmentName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
