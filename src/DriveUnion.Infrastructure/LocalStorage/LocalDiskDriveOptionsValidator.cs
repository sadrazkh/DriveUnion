using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// The check that stops a development backend from becoming a production one.
///
/// It runs at host start, not on the first upload, because the failure it guards against is silent
/// by nature: a box serving files from its own disk behaves exactly like one serving them from the
/// account pool, right up until the disk dies with the only copy of everybody's files on it. There
/// is no message the operator would see in between.
/// </summary>
internal sealed class LocalDiskDriveOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<LocalDiskDriveOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalDiskDriveOptions options)
    {
        // Off is the normal state, and nothing about a disabled backend is worth failing a boot for.
        if (!options.Enabled) return ValidateOptionsResult.Skip;

        if (environment.IsProduction())
        {
            return ValidateOptionsResult.Fail(
                $"{LocalDiskDriveOptions.SectionName}:Enabled is true in the Production environment. The "
                + "local-disk Drive backend keeps customers' files on this one box, unreplicated and "
                + "unbacked-up, and it exists only so the product can be demonstrated without a Google "
                + "Cloud project. Turn it off, or do not run this configuration in Production.");
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return ValidateOptionsResult.Fail(
                $"{LocalDiskDriveOptions.SectionName}:RootPath must name a directory when the local-disk "
                + "backend is enabled. It will not pick one on its own.");
        }

        if (options.SessionLifetime <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{LocalDiskDriveOptions.SectionName}:SessionLifetime must be positive; "
                + $"{options.SessionLifetime} would expire every upload session the moment it opened.");
        }

        return ValidateOptionsResult.Success;
    }
}
