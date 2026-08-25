using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Tests.Hosting;

/// <summary>
/// What an in-process host has to give up before it is safe to run beside the rest of the suite.
/// </summary>
public static class TestHostServices
{
    /// <summary>
    /// Takes every background loop out of a test host.
    ///
    /// <para><b>The race.</b> <c>Program.cs</c> starts the trash purge sweeper, the Telegram drainer,
    /// its update poller and its work-directory sweeper. Each opens its own scope on its own schedule.
    /// A harness owns exactly one SQLite connection — <c>:memory:</c>, kept open for the lifetime of
    /// the test so the schema survives — and disposes it when the test ends. A loop that opens a
    /// scope during that disposal reaches a connection halfway torn down, and the failure is a
    /// <c>NullReferenceException</c> raised inside <c>SqliteConnection.Close()</c> in the harness's
    /// own <c>Dispose</c>, with nothing in the stack naming the loop that caused it.</para>
    ///
    /// <para><b>Why it is a suite problem and not a file problem.</b> The loops belong to whichever
    /// host is running, and hosts from different suites run at the same time — so the exception lands
    /// on whichever harness happened to be tearing down at that moment, not on the one whose test
    /// added the pressure. Measured across full runs: it surfaced on <c>IdentityPagesHarness</c>,
    /// then on <c>PublicSiteHarness</c>, then on <c>PlanPageHarness</c>, roughly two runs in five,
    /// and every one of those tests passes when its file is run alone. That is what makes it worth a
    /// shared rule: a harness cannot fix this by looking at its own failures, because it does not
    /// have any.</para>
    ///
    /// <para><b>Why removing them all is right rather than blunt.</b> No test that renders a page or
    /// calls an endpoint is about a background loop. The three suites that <i>are</i> —
    /// <c>TrashRegistrationTests</c>, <c>LocalDiskRegistrationTests</c> and
    /// <c>GoogleServiceCollectionExtensionsTests</c> — assert over the service collection and never
    /// start a host, so they are untouched by this and stay the place where a missing
    /// <c>AddHostedService</c> is caught.</para>
    ///
    /// <para>Removal by service type rather than by implementation type on purpose: a loop added
    /// later is covered without anybody remembering to add it here, which is the failure mode of the
    /// version of this that named the sweeper.</para>
    /// </summary>
    public static IServiceCollection RemoveEveryBackgroundLoop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(descriptor);
        }

        return services;
    }
}
