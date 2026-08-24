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
}
