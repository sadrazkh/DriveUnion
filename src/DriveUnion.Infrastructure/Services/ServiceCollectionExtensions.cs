using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Services;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The layer between the database and the controllers.
    ///
    /// Everything here is scoped, because it all shares the request's
    /// <see cref="Persistence.DriveUnionDbContext"/>. <see cref="IDriveClient"/> is deliberately not
    /// registered by this method: the tests supply a fake and the Google integration supplies the
    /// real one, and nothing in this file should decide which.
    /// </summary>
    public static IServiceCollection AddDriveUnionServices(this IServiceCollection services)
    {
        // TryAdd throughout: a caller that has already chosen a clock or a slug generator — a test,
        // or the Google integration wiring its own — keeps its choice.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISlugGenerator, SlugGenerator>();

        services.TryAddScoped<IFileCatalog, FileCatalog>();
        services.TryAddScoped<IShareLinkService, ShareLinkService>();
        services.TryAddScoped<IPublicLinkReader, PublicLinkReader>();
        services.TryAddScoped<IUploadCoordinator, UploadCoordinator>();
        services.TryAddScoped<IUploadTargetSelector, SingleAccountUploadTargetSelector>();

        return services;
    }
}
