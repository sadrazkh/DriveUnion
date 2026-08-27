using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Push;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Services;
using DriveUnion.Web.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// The lines a host adds for notifications, and why they are three rather than one.
///
/// <para>Without the loop every device still subscribes, every event is still raised, and not one
/// notification is ever delivered — a failure with no symptom on any screen, which is exactly the
/// kind this suite is asked to catch before a deployment does. It is the same shape as
/// <c>DeletionRegistrationTests</c>, and the loop is separate for the same reason: every in-process
/// test host boots the real pipeline over one shared SQLite connection, and a background loop
/// opening scopes against it turns unrelated suites into «database is locked».</para>
/// </summary>
public class PushRegistrationTests
{
    [Fact]
    public async Task Every_half_resolves_from_the_registrations_a_host_makes()
    {
        await using var harness = ServiceTestHarness.Create();

        await using var provider = Provider(harness);
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IPushSubscriptions>()
            .Should().BeOfType<PushSubscriptionStore>();

        scope.ServiceProvider.GetRequiredService<IPushDispatcher>().Should().BeOfType<PushDispatcher>();
        scope.ServiceProvider.GetRequiredService<IWebPushSender>().Should().BeOfType<WebPushSender>();
        scope.ServiceProvider.GetRequiredService<IPushEvents>().Should().BeOfType<PushOutbox>();
    }

    /// <summary>
    /// <b>The doorbell and the thing listening to it are the same object.</b>
    ///
    /// <para>Two registrations of <see cref="PushOutbox"/> would compile, resolve, and deliver
    /// nothing: the domain would write into one channel and the worker would read an empty one, for
    /// ever, with no exception anywhere. That is the whole reason the second registration is a
    /// factory over the first rather than a second <c>TryAddSingleton</c>.</para>
    /// </summary>
    [Fact]
    public async Task The_outbox_the_domain_writes_to_is_the_one_the_worker_reads()
    {
        await using var harness = ServiceTestHarness.Create();
        await using var provider = Provider(harness);

        provider.GetRequiredService<IPushEvents>()
            .Should().BeSameAs(provider.GetRequiredService<PushOutbox>());
    }

    /// <summary>
    /// The doorbell comes with the application layer; delivering is its own line.
    ///
    /// <para>Deliberate: raising an event costs nothing and depends on nothing, so any host that has
    /// the domain can raise. Delivering needs the network and the operator's VAPID keys, and a host
    /// that has neither — every in-process test host in this suite — should be able to say so by
    /// leaving the line out rather than by having a worker it must then remove.</para>
    /// </summary>
    [Fact]
    public void The_domain_can_raise_without_anything_being_registered_to_deliver()
    {
        var services = new ServiceCollection();

        services.AddDriveUnionServices();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IPushEvents>().Should().NotBeNull();
        provider.GetService<IPushDispatcher>().Should().BeNull("delivering is a separate line");
    }

    [Fact]
    public void The_loop_is_a_separate_line()
    {
        var services = new ServiceCollection();

        services.AddDriveUnionPush();

        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));

        services.AddDriveUnionPushWorker();

        services.Should().ContainSingle(
            d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PushWorker));
    }

    private static ServiceProvider Provider(ServiceTestHarness harness)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddSingleton<IDriveClient>(harness.Drive);
        services.AddScoped(_ => harness.NewContext());

        // The two the web project supplies, because the words belong to it and the keys belong to
        // the deployment. Program.cs registers exactly these two beside AddDriveUnionPush().
        services.AddSingleton<IVapidCredentials>(
            _ => new VapidCredentials(new ConfigurationBuilder().Build()));

        services.AddScoped<IPushMessages, PushMessages>();

        services.AddDriveUnionServices();
        services.AddDriveUnionPush();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
