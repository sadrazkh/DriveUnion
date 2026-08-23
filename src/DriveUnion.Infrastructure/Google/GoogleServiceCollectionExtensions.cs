using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddOptions<GoogleOAuthOptions>()
            .Bind(configuration.GetSection(GoogleOAuthOptions.SectionName))
            .Validate(
                o => o.IsConfigured(),
                "Google:ClientId, Google:ClientSecret and Google:RedirectUri must all be set before "
                + "an account can be connected or refreshed.");

        // Deliberately no ValidateOnStart. The panel has to boot without Google credentials — that
        // is the state this product is developed in, and it is also every integration test that has
        // no business knowing Google exists. The check fires on the first request that needs them.

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenProtector, DataProtectionTokenProtector>();

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
