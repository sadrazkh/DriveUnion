using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Google;

public static class GoogleServiceCollectionExtensions
{
    /// <summary>
    /// Everything that talks to Google, plus the protector that keeps its credentials encrypted at
    /// rest.
    ///
    /// Call it after <c>AddDataProtection()</c> and <c>AddDbContext&lt;DriveUnionDbContext&gt;()</c>:
    /// the token protector needs the key ring, and the credentials and the token service both need
    /// the tables.
    /// </summary>
    public static IServiceCollection AddGoogleDrive(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(GoogleOAuthOptions.SectionName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenProtector, DataProtectionTokenProtector>();

        // Where the OAuth clients the operator types into the panel are kept: rows, with their
        // secrets encrypted by the key ring that is itself a table. This used to be a JSON file
        // inside the container, and a redeploy deleting it took the whole pool down with it — the
        // refresh tokens survived and could not be refreshed. See GoogleOAuthClient.
        //
        // A singleton, because the resolver below is one and IOptions<T> is read synchronously; it
        // opens its own scope per call rather than holding a context.
        services.TryAddSingleton<IGoogleOAuthClientStore, GoogleOAuthClientStore>();

        services.TryAddSingleton(sp => new GoogleOAuthCredentialResolver(
            section,
            sp.GetRequiredService<IGoogleOAuthClientStore>()));

        // ────────────────────────────────────────────────────────────────────────────────────────
        // This replaces the ordinary AddOptions().Bind() for GoogleOAuthOptions, and it is not an
        // accident. Binding computes the options once, while the container is being built; the
        // operator's client id arrives later, by hand, into a panel that is already running. An
        // explicit closed-generic registration outranks the open generic IOptions<> that the
        // options infrastructure provides, so every existing consumer keeps asking for exactly what
        // it asked for before and starts getting an answer that is resolved per read.
        //
        // Configuration still wins over the panel, field by field. See
        // GoogleOAuthCredentialResolver for why that direction and not the other.
        //
        // Still no ValidateOnStart, and now no Validate at all: the resolver never throws, and the
        // first honest complaint about missing credentials is the one GoogleTokenService makes at
        // the request that needed them, naming the three settings.
        // ────────────────────────────────────────────────────────────────────────────────────────
        services.TryAddSingleton<IOptions<GoogleOAuthOptions>>(
            sp => sp.GetRequiredService<GoogleOAuthCredentialResolver>());

        services.TryAddSingleton<IGoogleOAuthCredentials>(
            sp => sp.GetRequiredService<GoogleOAuthCredentialResolver>());

        // The one-time carry of App_Data/google-oauth.json into the table above. It walks straight
        // back out when there is no file, which is every deployment that has already lost one and
        // every test host in this repository — so registering it unconditionally costs nothing.
        services.AddHostedService(sp => new GoogleOAuthClientImport(
            LegacyCredentialFilePath(sp, section),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ITokenProtector>(),
            sp.GetRequiredService<IGoogleOAuthClientStore>(),
            sp.GetRequiredService<ILogger<GoogleOAuthClientImport>>()));

        services.AddTransient<DriveRetryHandler>();

        services.AddHttpClient(GoogleTokenService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateGoogleHandler)
            .AddHttpMessageHandler<DriveRetryHandler>();

        services.AddHttpClient<GoogleDriveClient>(client =>
            {
                // HttpClient.Timeout is a whole-operation deadline that keeps running while the
                // response body is being read, even under ResponseHeadersRead. Any finite value here
                // is a cap on how large a file this product can serve, so the deadline is the
                // caller's CancellationToken — which is the client disconnecting — and not a clock.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(CreateGoogleHandler)
            .AddHttpMessageHandler<DriveRetryHandler>();

        services.AddTransient<IDriveClient>(sp => sp.GetRequiredService<GoogleDriveClient>());
        services.AddTransient<IGoogleAboutReader>(sp => sp.GetRequiredService<GoogleDriveClient>());

        // Singleton, because the single-flight gate that turns twenty concurrent chunk uploads into
        // one token refresh only works if every request in the process shares one instance.
        services.AddSingleton<IGoogleTokenService, GoogleTokenService>();

        services.AddScoped<IGoogleAccountDirectory, GoogleAccountDirectory>();

        // What the accounts screen shows beyond the pool's own summary: which client connected each
        // account, and why it last stopped working.
        services.AddScoped<IGoogleClientUsageReader, GoogleClientUsageReader>();

        return services;
    }

    /// <summary>
    /// Where the OAuth client used to be written, and the only place
    /// <see cref="GoogleOAuthClientImport"/> looks.
    ///
    /// <c>Google:CredentialStorePath</c> still overrides it, because a deployment that took that
    /// advice and put the file on a volume is exactly the deployment that still has one to import.
    /// The default is the content root, not <c>AppContext.BaseDirectory</c>: that is where the old
    /// store wrote, and a path that does not match it would import nothing and say nothing.
    /// </summary>
    private static string LegacyCredentialFilePath(IServiceProvider services, IConfiguration section)
    {
        if (section["CredentialStorePath"] is { } configured && !string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var root = services.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;

        return Path.Combine(root, "App_Data", "google-oauth.json");
    }

    private static SocketsHttpHandler CreateGoogleHandler() => new()
    {
        // A 308 from a resumable session means "resume incomplete", not "moved permanently". It
        // carries no Location, so the redirect logic has nothing to chase — but leaving it on is an
        // invitation for a future Google response to be followed instead of read.
        AllowAutoRedirect = false,

        // No automatic decompression. The download path mirrors Drive's Content-Length and
        // Content-Range straight back to the client; a transparently decompressed body would make
        // both of them lie, and byte ranges are the whole reason video seeking works.
        AutomaticDecompression = System.Net.DecompressionMethods.None,

        // Connecting must still fail fast even though the overall request has no deadline.
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };
}
