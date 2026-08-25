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

        // The one piece of state in this layer that outlives a request, and it has to: a folder id
        // cached per request is a folder id resolved per request, which is the pair of Drive calls
        // the cache exists to stop making. The resolver over it stays scoped, because it reads the
        // tenant's slug through the request's database context.
        services.TryAddSingleton<DriveFolderCache>();

        services.TryAddScoped<IDriveFolders, DriveFolders>();
        services.TryAddScoped<IFileCatalog, FileCatalog>();

        // The customer's folder tree, beside the catalogue rather than in a slice of its own: it
        // reads the same rows through the same DbContext, and every screen that draws one draws the
        // other. Not to be confused with IDriveFolders above, which is the operator's Drive layout.
        services.TryAddScoped<IFolderTree, FolderTree>();

        // Labels, which cut across the tree rather than nesting inside it. Beside it for the same
        // reason: same rows, same DbContext, same screen.
        services.TryAddScoped<ITags, TagStore>();

        // The egress counter. Written on the public download path and read by the capacity card and
        // both dashboards — the three places that said «— / ۵۰۰ GB» because nothing counted.
        services.TryAddScoped<ITrafficMeter, TrafficMeter>();
        services.TryAddScoped<IShareLinkService, ShareLinkService>();
        services.TryAddScoped<IPublicLinkReader, PublicLinkReader>();
        services.TryAddScoped<IUploadCoordinator, UploadCoordinator>();
        services.TryAddScoped<IUploadTargetSelector, SingleAccountUploadTargetSelector>();

        return services;
    }
}
