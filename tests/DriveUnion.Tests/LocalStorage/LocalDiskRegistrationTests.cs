using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.LocalStorage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.LocalStorage;

/// <summary>
/// The switch, and the refusal.
///
/// A file host serving customers' files off its own disk looks exactly like one serving them from the
/// account pool — right up to the morning the disk dies with the only copy of everything on it. There
/// is no symptom in between, so the guard cannot be a convention: the backend is off unless a
/// deployment names it, and the host refuses to start with it on in Production.
/// </summary>
public class LocalDiskRegistrationTests
{
    [Fact]
    public void Nothing_is_registered_unless_a_deployment_asks_for_it()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Development");

        services.AddLocalDiskDrive(Configuration(enabled: false, harness.Root));

        // Not registered at all, rather than registered and inert: with Google's client in the
        // container this is the difference between a demo backend and a production one.
        services.Should().NotContain(d => d.ServiceType == typeof(IDriveClient));
    }

    [Fact]
    public void Enabling_it_takes_the_Drive_client_over_rather_than_shadowing_it()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Development");

        // Stands in for AddGoogleDrive's registration. If it survived, resolving every IDriveClient
        // would throw — which is the point: two of them and a resolution order nobody re-reads is how
        // customers' files end up somewhere nobody chose.
        services.AddSingleton<IDriveClient>(_ =>
            throw new InvalidOperationException("The Google client should have been removed."));

        services.AddLocalDiskDrive(Configuration(enabled: true, harness.Root));

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IDriveClient>().Should().ContainSingle()
            .Which.Should().BeOfType<LocalDiskDriveClient>();
    }

    [Fact]
    public void The_root_is_created_when_the_client_is()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Development");
        services.AddLocalDiskDrive(Configuration(enabled: true, harness.Root));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDriveClient>();

        client.Should().BeOfType<LocalDiskDriveClient>()
            .Which.RootPath.Should().Be(harness.Root);

        Directory.Exists(harness.Root).Should().BeTrue();
    }

    [Fact]
    public void Production_refuses_to_start_with_the_local_disk_enabled()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Production");
        services.AddLocalDiskDrive(Configuration(enabled: true, harness.Root));

        using var provider = services.BuildServiceProvider();

        // This is what the host calls before it starts anything. The message has to say why, because
        // whoever set the flag believed it was harmless.
        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        start.Should().Throw<OptionsValidationException>()
            .WithMessage("*Production*")
            .WithMessage("*unreplicated*");
    }

    [Fact]
    public void Production_is_untouched_when_the_backend_is_off()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Production");
        services.AddLocalDiskDrive(Configuration(enabled: false, harness.Root));

        using var provider = services.BuildServiceProvider();
        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        // The guard must not make a normal production boot fail. Off is the default and the default
        // has to be boring.
        start.Should().NotThrow();
    }

    [Fact]
    public void In_production_the_backend_cannot_even_be_resolved()
    {
        using var harness = new LocalDiskHarness();
        var services = Collection("Production");
        services.AddLocalDiskDrive(Configuration(enabled: true, harness.Root));

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IDriveClient>();

        // The same validation the host runs at start also runs the first time the options are read,
        // so a Production process cannot obtain this client even if it never called the validator.
        resolve.Should().Throw<OptionsValidationException>().WithMessage("*Production*");
    }

    [Fact]
    public async Task A_backend_that_got_past_validation_still_refuses_to_run_in_production()
    {
        using var harness = new LocalDiskHarness();

        // Options.Create bypasses IValidateOptions entirely, which is the state a deployment that
        // switched validation off would be in. Two locks on the same door, and this is the second.
        var announcement = new LocalDiskDriveAnnouncement(
            harness.Create(),
            new StubHostEnvironment("Production"),
            NullLogger<LocalDiskDriveAnnouncement>.Instance);

        var start = async () => await announcement.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*refuses to start*");
    }

    [Fact]
    public async Task Starting_says_out_loud_that_the_files_are_on_this_box()
    {
        using var harness = new LocalDiskHarness();
        var logs = new List<(LogLevel Level, string Message)>();

        var services = Collection("Development");
        services.AddLogging(builder => builder.AddProvider(new CapturingLoggerProvider(logs)));
        services.AddLocalDiskDrive(Configuration(enabled: true, harness.Root));

        using var provider = services.BuildServiceProvider();
        // Named rather than taken as the only one. This feature registers a second hosted service —
        // the pool account, without which every upload is refused before the disk is reached — and a
        // Single() here would fail on a change that is nothing to do with what this test asserts.
        await provider.GetServices<IHostedService>()
            .OfType<LocalDiskDriveAnnouncement>()
            .Single()
            .StartAsync(CancellationToken.None);

        // A warning, not information: the only place anybody finds out where the bytes went is the
        // log, and a line nobody notices is the same as no line.
        logs.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("LOCAL DISK").And.Contain(harness.Root);
    }

    private static ServiceCollection Collection(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environmentName));

        return services;
    }

    private static IConfiguration Configuration(bool enabled, string rootPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DriveUnion:LocalDisk:Enabled"] = enabled ? "true" : "false",
                ["DriveUnion:LocalDisk:RootPath"] = rootPath,
            })
            .Build();

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "DriveUnion.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel Level, string Message)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                sink.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
