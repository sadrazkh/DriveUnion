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
    /// the token protector needs the key ring and the token service needs the accounts table.
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

        // Where the OAuth client the operator types into the panel is kept. Registered as a factory
        // rather than by type so that IHostEnvironment can be optional: this same registration has
        // to resolve inside a bare ServiceCollection in the tests, which has no host at all.
        services.TryAddSingleton<IGoogleOAuthCredentialStore>(sp => new FileGoogleOAuthCredentialStore(
            CredentialStorePath(sp, section),
            sp.GetRequiredService<ITokenProtector>(),
            sp.GetRequiredService<ILogger<FileGoogleOAuthCredentialStore>>()));

        services.TryAddSingleton(sp => new GoogleOAuthCredentialResolver(
            section,
            sp.GetRequiredService<IGoogleOAuthCredentialStore>()));

        // ────────────────────────────────────────────────────────────────────────────────────────
        // This replaces the ordinary AddOptions().Bind() for GoogleOAuthOptions, and it is not an
        // accident. Binding computes the options once, while the container is being built; the
        // operator's client id arrives later, by hand, into a panel that is already running. An
        // explicit closed-generic registration outranks the open generic IOptions<> that the
        // options infrastructure provides, so every existing consumer — GoogleTokenService, the
        // accounts controller — keeps asking for exactly what it asked for before and starts
        // getting an answer that is resolved per read.
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

        return services;
    }

    /// <summary>
    /// Where <see cref="FileGoogleOAuthCredentialStore"/> writes.
    ///
    /// <c>Google:CredentialStorePath</c> overrides it, which is how a deployment puts the file on a
    /// volume that outlives the container — worth doing, because unlike the Data Protection key
    /// ring this file is not in the database and a redeploy that loses it costs the operator a
    /// re-paste. The default is the content root, not <c>AppContext.BaseDirectory</c>: in
    /// development the latter is <c>bin/Debug</c>, and <c>dotnet clean</c> would take the
    /// credentials with it.
    /// </summary>
    private static string CredentialStorePath(IServiceProvider services, IConfiguration section)
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
