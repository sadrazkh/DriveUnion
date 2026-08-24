using DriveUnion.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.LocalStorage;

public static class LocalDiskDriveServiceCollectionExtensions
{
    /// <summary>
    /// Swaps Google Drive for one box's disk, when a deployment explicitly asks for it.
    ///
    /// Configuration:
    /// <list type="bullet">
    /// <item><c>DriveUnion:LocalDisk:Enabled</c> — <c>false</c> unless set. Nothing below runs while
    /// it is false, and the Google client stays registered.</item>
    /// <item><c>DriveUnion:LocalDisk:RootPath</c> — where the files go. Required when enabled.</item>
    /// <item><c>DriveUnion:LocalDisk:SessionLifetime</c> — how long a resumable session lives,
    /// <c>7.00:00:00</c> by default, which is Drive's own week.</item>
    /// </list>
    ///
    /// Call it <em>after</em> <c>AddGoogleDrive</c>: enabling the local disk removes the existing
    /// <see cref="IDriveClient"/> registration rather than shadowing it, so that a resolution order
    /// nobody re-reads cannot decide where customers' files end up. The Google account directory and
    /// token service are left alone — connecting an account still means connecting a real one.
    ///
    /// The host refuses to start in <c>Production</c> whether this is enabled by configuration or by
    /// accident; see <see cref="LocalDiskDriveOptionsValidator"/>.
    /// </summary>
    public static IServiceCollection AddLocalDiskDrive(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(LocalDiskDriveOptions.SectionName);

        services.AddOptions<LocalDiskDriveOptions>().Bind(section).ValidateOnStart();

        // Registered even when the backend is off, because the environment check has to fire on the
        // configuration that turns it on — including one that arrives from an environment variable
        // on a box nobody meant to enable it on.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<LocalDiskDriveOptions>, LocalDiskDriveOptionsValidator>());

        if (!section.GetValue("Enabled", false)) return services;

        services.TryAddSingleton(TimeProvider.System);

        // Singleton: it owns the per-session gates that keep a chunk write indivisible, and those
        // only work if every request in the process shares one instance.
        services.AddSingleton<LocalDiskDriveClient>();

        services.RemoveAll<IDriveClient>();
        services.AddSingleton<IDriveClient>(sp => sp.GetRequiredService<LocalDiskDriveClient>());

        services.AddHostedService<LocalDiskDriveAnnouncement>();

        // Replacing IDriveClient is not enough on its own: the upload path looks for an account, not
        // for a client. Without a row in the pool every upload is refused before the disk is ever
        // reached, which is what it did.
        services.AddHostedService<LocalDiskPoolAccount>();

        return services;
    }
}
