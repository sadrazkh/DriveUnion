using DriveUnion.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// The two addresses a platform asks this container about itself.
///
/// <para><b>Liveness and readiness are different questions and the difference is the whole point.</b>
/// «Is this process alive?» is answered by <see cref="Live"/>, and an orchestrator that gets a
/// failure to it kills the container. «Should this process be sent traffic?» is answered by
/// <see cref="Ready"/>, and a failure to that one takes the container out of rotation and leaves it
/// running. Wiring the same checks to both is the classic mistake and it is not a subtle one: a
/// Postgres that blips for four seconds would fail liveness on every replica at once, the platform
/// would kill every one of them, and a database wobble nobody would have noticed becomes a restart
/// storm and an outage. So <see cref="Live"/> runs no checks at all — <c>Predicate</c> selects
/// none — and answering it is proof of exactly what it claims: that this process is up, holds a
/// thread and can route a request.</para>
///
/// <para><b>Both are anonymous, so neither may say anything.</b> A probe address is reachable by
/// whoever can reach the container, which on a shared platform is more than the operator. The body
/// is check names and statuses; the exception, the description and the <c>Data</c> bag every
/// <see cref="HealthCheckResult"/> can carry are never written, and that is a rule about the writer
/// rather than about today's three checks — the next check somebody adds will put a connection
/// string in a description sooner or later, and this is where that stops being a leak.</para>
///
/// <para>ASP.NET's own health-check infrastructure rather than a controller, because
/// <c>MapHealthChecks</c> already owns the parts that are easy to get wrong: a status code mapped
/// from an aggregate, checks run in parallel inside one scope, and a failing check that throws
/// turned into an unhealthy report rather than a 500.</para>
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Liveness. Touches nothing — see the class remarks.</summary>
    public const string Live = "/healthz";

    /// <summary>Readiness: the database, the Google pool and the background loops.</summary>
    public const string Ready = "/readyz";

    /// <summary>What <see cref="Ready"/> selects on. <see cref="Live"/> selects nothing.</summary>
    public const string ReadyTag = "ready";

    public const string DatabaseCheck = "database";

    public const string GooglePoolCheck = "google-pool";

    public const string WorkerLoopCheck = "worker-loops";

    /// <summary>
    /// How long a background loop may be quiet before this deployment stops being ready.
    ///
    /// <para>Five minutes. The slowest loop that reports in idles for one, so this is five missed
    /// turns rather than one — a probe that goes red because a pass ran a few seconds late would be
    /// a probe an operator learns to ignore, and one that is ignored is one that is not there. The
    /// argument for each end of it, and the list of which loops report at all, is on
    /// <see cref="WorkerHeartbeat"/>.</para>
    /// </summary>
    public static readonly TimeSpan WorkerLoopWindow = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddDriveUnionHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A singleton because the cached pool answer has to be the same object for every probe —
        // see GooglePoolTokenHealthCheck. Registered here rather than left to the health-check
        // builder, whose AddCheck<T> would otherwise construct a fresh one per probe and cache
        // nothing.
        services.AddSingleton<GooglePoolTokenHealthCheck>();

        services.AddHealthChecks()
            .AddCheck<DatabaseReachableHealthCheck>(DatabaseCheck, tags: [ReadyTag])
            .AddCheck<GooglePoolTokenHealthCheck>(GooglePoolCheck, tags: [ReadyTag])
            .AddCheck(
                WorkerLoopCheck,
                new WorkerLoopHealthCheck(WorkerHeartbeat.Silences, WorkerLoopWindow),
                failureStatus: null,
                tags: [ReadyTag]);

        return services;
    }

    public static void MapDriveUnionHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // No checks, on purpose: this answers whether the process is up, and a liveness probe that
        // consults a dependency turns that dependency's bad minute into a killed container.
        endpoints.MapHealthChecks(Live, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks(Ready, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
        }).AllowAnonymous();
    }

    /// <summary>
    /// Check names and statuses. Nothing else, ever.
    ///
    /// <para>Not <c>entry.Description</c>, not <c>entry.Exception</c>, not <c>entry.Data</c>, not
    /// <c>report.TotalDuration</c> and not a version. The framework's own default writer says only
    /// the aggregate word, which leaks nothing and also tells an operator nothing about which of
    /// three dependencies is down; this is the smallest step past that which is still safe to hand
    /// an anonymous caller. Every one of the omissions above is a place a future check would put a
    /// connection string, a pool address or a stack trace without meaning to.</para>
    ///
    /// <para><c>no-store</c> because a cached readiness answer is worse than none: a proxy holding
    /// «Healthy» for sixty seconds is a container the platform keeps sending traffic to after it has
    /// stopped being able to serve it.</para>
    /// </summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var body = new HealthBody(
            report.Status.ToString(),
            report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString(),
                StringComparer.Ordinal));

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }

    /// <param name="Status">The aggregate — <c>Healthy</c> or <c>Unhealthy</c>.</param>
    /// <param name="Checks">Check name to status. Empty for <see cref="Live"/>, which runs none.</param>
    private sealed record HealthBody(string Status, IReadOnlyDictionary<string, string> Checks);
}
