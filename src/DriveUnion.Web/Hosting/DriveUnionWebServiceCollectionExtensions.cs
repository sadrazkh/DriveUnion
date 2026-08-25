using System.Globalization;
using System.Threading.RateLimiting;
using DriveUnion.Web.Controllers;
using DriveUnion.Web.Security;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Everything the HTTP surface needs in the container: its options, the authorisation policies the
/// panel and the operator screens are guarded by, and the limiter for <c>/d/*</c>.
/// </summary>
public static class DriveUnionWebServiceCollectionExtensions
{
    public static IServiceCollection AddDriveUnionWeb(this IServiceCollection services)
    {
        services.AddOptions<DriveUnionWebOptions>().BindConfiguration(DriveUnionWebOptions.SectionName);

        // Singleton so the fallback key stays stable for the life of the process — a transient
        // hasher would give every request its own key and every download its own "party".
        services.AddSingleton<IDownloadIpHasher, DownloadIpHasher>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                DriveUnionPolicies.Tenant,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => context.User.GetTenantId() is not null));

            options.AddPolicy(
                DriveUnionPolicies.Operator,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue));

            // The API's two, and both name their scheme.
            //
            // Without AuthenticationSchemes the policy would accept the application's default —
            // the cookie — and every /api/v1 route would quietly become reachable from a browser
            // session. That is not a hole a customer could walk through by accident, but it is a
            // second way in that nothing documents and no test covers, which is how a route ends up
            // being defended by a rule its author did not know applied to it.
            options.AddPolicy(
                ApiPolicies.Read,
                policy => policy
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => context.User.GetTenantId() is not null));

            options.AddPolicy(
                ApiPolicies.Write,
                policy => policy
                    .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.GetTenantId() is not null && ApiPolicies.HasWrite(context.User)));
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // A person reads one landing page. Sixty a minute leaves room for refreshes and the
            // language links while making a slug walk from one address pointless.
            options.AddPolicy(
                DriveUnionRateLimits.PublicPage,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    ClientPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // A bucket, not a window: scrubbing a video fires a burst of ranged requests that a
            // window would reject outright, while the sustained rate a scanner needs is what the
            // refill actually caps.
            options.AddPolicy(
                DriveUnionRateLimits.PublicDownload,
                context => RateLimitPartition.GetTokenBucketLimiter(
                    ClientPartitionKey(context),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        TokensPerPeriod = 60,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            // The API, partitioned on the key rather than on the address — see DriveUnionRateLimits.
            //
            // A token bucket and not a window, for the reason the public download uses one: a script
            // listing a folder and then fetching forty files is a legitimate burst, and a fixed
            // window would refuse the fortieth for being in the wrong second. 600 an hour sustained
            // with 120 in hand covers everything a customer's own automation does and bounds a
            // runaway loop long before it reaches Google.
            options.AddPolicy(
                DriveUnionRateLimits.Api,
                context => RateLimitPartition.GetTokenBucketLimiter(
                    ApiPartitionKey(context),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        TokensPerPeriod = 10,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            // Telegram's own retries land here, so the limit is generous — it exists to bound a
            // forged flood at an endpoint that answers before it does any work, not to shape
            // Telegram. Partitioned on the peer address, which behind the loopback Bot API server
            // is one key; that is the point, because nothing else should be reaching it.
            options.AddPolicy(
                TelegramWebhookController.RateLimitPolicy,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    ClientPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// The connection's own address. Behind the OVH proxy this is the proxy unless forwarded
    /// headers are honoured in the pipeline — in which case every visitor shares one partition and
    /// the limiter protects nothing.
    /// </summary>
    private static string ClientPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The presented key, hashed, or the address when there is none.
    ///
    /// <para>Hashed because a partition key is a dictionary key held in memory for the life of the
    /// window, and a bearer secret sitting in one is a secret in one more place than it has to be.
    /// Not a security boundary — it is a bucket name — but there is no reason for it to be the token
    /// itself.</para>
    ///
    /// <para>The limiter runs before authentication, so this reads the header rather than the
    /// principal: by the time <c>User</c> is populated the request has already been admitted or
    /// refused. A malformed or dead key therefore shares the address's bucket, which is the right
    /// answer — that is exactly the traffic a limit is for.</para>
    /// </summary>
    private static string ApiPartitionKey(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ClientPartitionKey(context);
        }

        var presented = header["Bearer ".Length..].Trim();

        return presented.Length == 0
            ? ClientPartitionKey(context)
            : Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(presented)));
    }
}
