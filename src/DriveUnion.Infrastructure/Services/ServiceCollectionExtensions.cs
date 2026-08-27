using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.S3;
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

        // What a browser needs to open an encrypted file — and nothing that opens one.
        services.TryAddScoped<IFileEncryption, FileEncryptionStore>();

        // The operator's pool: what is on each account, and the one thing that can move a file
        // between two of them. The background loop that drives the second is registered separately
        // — see AccountMigrationWorker for why a hosted service must not come out of here.
        services.TryAddScoped<IAccountMigrations, AccountMigrations>();
        services.TryAddScoped<IAccountMigrator, AccountMigrator>();

        // The egress counter. Written on the public download path and read by the capacity card and
        // both dashboards — the three places that said «— / ۵۰۰ GB» because nothing counted.
        services.TryAddScoped<ITrafficMeter, TrafficMeter>();

        // …and the thing that reads it back as a yes or a no, before Google is contacted. Separate
        // from the meter because it compares against a ceiling on another table — see
        // IEgressAllowance — and because the meter must stay a writer on the download path.
        services.TryAddScoped<IEgressAllowance, EgressAllowanceReader>();

        // The API's keys, and the server-only lookup that turns a file id into somewhere to stream
        // from — see IStoredFileBytes for why that is not a method on IFileCatalog.
        services.TryAddScoped<IApiTokens, ApiTokenStore>();
        services.TryAddScoped<IStoredFileBytes, StoredFileBytesReader>();

        // The S3 gateway: access keys, and object keys over the folder tree.
        services.TryAddScoped<IS3Credentials, S3CredentialStore>();
        services.TryAddScoped<IS3Objects, S3ObjectReader>();

        // Multipart, and the volume it stages on. Off unless a staging directory is configured —
        // see S3StagingOptions for why that is not defaulted to a temporary path.
        services.AddOptions<S3StagingOptions>().BindConfiguration(S3StagingOptions.SectionName);
        services.TryAddSingleton<S3StagingDirectory>();
        services.TryAddScoped<IS3Multipart, S3MultipartStore>();
        services.TryAddScoped<IShareLinkService, ShareLinkService>();
        // Two interfaces, one class: an anonymous visitor writes a row and an operator reads it.
        // Registered separately so the anonymous surface cannot reach the operator.s — a controller
        // has to ask for IAbuseQueue by name, which is a line a reader will notice.
        services.TryAddScoped<AbuseReports>();
        services.TryAddScoped<IAbuseReports>(sp => sp.GetRequiredService<AbuseReports>());
        services.TryAddScoped<IAbuseQueue>(sp => sp.GetRequiredService<AbuseReports>());

        // Where «this finished» is put down. A singleton, and here rather than in
        // AddDriveUnionPush(), because it is the half every raiser in this layer needs and it costs
        // nothing: an in-memory queue with no dependencies. Delivering out of it needs the network
        // and the operator's VAPID keys and is a separate line, so a host can have the domain
        // raising events without a background loop opening scopes against a shared SQLite
        // connection. Both registrations resolve the one instance — two would be a doorbell nobody
        // is listening to.
        services.TryAddSingleton<Push.PushOutbox>();
        services.TryAddSingleton<IPushEvents>(sp => sp.GetRequiredService<Push.PushOutbox>());

        services.TryAddScoped<IPublicLinkReader, PublicLinkReader>();
        services.TryAddScoped<IUploadCoordinator, UploadCoordinator>();
        services.TryAddScoped<IUploadTargetSelector, SingleAccountUploadTargetSelector>();

        return services;
    }
}
