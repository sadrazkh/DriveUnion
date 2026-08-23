using DriveUnion.Infrastructure.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// The claims factory that makes the panel reachable, and the seeder that puts the first
    /// operator in an empty database.
    ///
    /// Safe on either side of <c>AddIdentity</c>: the existing factory registration is removed
    /// first, so this does not depend on which call happens to run last.
    /// </summary>
    public static IServiceCollection AddDriveUnionIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.RemoveAll<IUserClaimsPrincipalFactory<AppUser>>();
        services.AddScoped<IUserClaimsPrincipalFactory<AppUser>, DriveUnionClaimsPrincipalFactory>();

        services.AddOptions<DriveUnionSeedOptions>()
            .Bind(configuration.GetSection(DriveUnionSeedOptions.SectionName));

        // Also registered by AddGoogleDrive; TryAdd so the order of the two calls does not matter.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IdentitySeeder>();

        return services;
    }
}
