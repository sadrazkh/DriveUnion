using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Google;

/// <summary>
/// Proves the one line Program.cs has to add actually resolves. A container that only fails at the
/// first request is a container that fails in front of the operator.
/// </summary>
public class GoogleServiceCollectionExtensionsTests
{
    [Fact]
    public void Everything_the_panel_asks_for_can_be_resolved()
    {
        using var provider = Build(Configured());
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDriveClient>().Should().BeOfType<GoogleDriveClient>();
        scope.ServiceProvider.GetRequiredService<IGoogleAboutReader>().Should().BeOfType<GoogleDriveClient>();
        scope.ServiceProvider.GetRequiredService<IGoogleTokenService>().Should().BeOfType<GoogleTokenService>();
        scope.ServiceProvider.GetRequiredService<IGoogleAccountDirectory>()
            .Should().BeOfType<GoogleAccountDirectory>();
        scope.ServiceProvider.GetRequiredService<ITokenProtector>().Should().NotBeNull();

        // The OAuth clients, and what the accounts screen asks about them. Both arrive with
        // AddGoogleDrive so that moving the credentials into the database costs Program.cs nothing.
        scope.ServiceProvider.GetRequiredService<IGoogleOAuthClientStore>()
            .Should().BeOfType<GoogleOAuthClientStore>();
        scope.ServiceProvider.GetRequiredService<IGoogleClientUsageReader>()
            .Should().BeOfType<GoogleClientUsageReader>();

        // The one-time carry of the retired App_Data/google-oauth.json, registered unconditionally
        // because it walks straight back out when there is no file.
        scope.ServiceProvider.GetServices<IHostedService>()
            .Should().ContainSingle(service => service is GoogleOAuthClientImport);
    }

    [Fact]
    public void The_token_service_is_one_object_for_the_whole_process()
    {
        using var provider = Build(Configured());

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        // If these were two instances the single-flight gate would be two gates, and twenty
        // concurrent chunk uploads would be twenty refreshes again.
        first.ServiceProvider.GetRequiredService<IGoogleTokenService>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IGoogleTokenService>());
    }

    [Fact]
    public void The_configuration_section_is_Google()
    {
        using var provider = Build(Configured());

        var options = provider.GetRequiredService<IOptions<GoogleOAuthOptions>>().Value;

        options.ClientId.Should().Be("client-id.apps.googleusercontent.com");
        options.ClientSecret.Should().Be("client-secret");
        options.RedirectUri.Should().Be("https://drive.example/oauth/google");
    }

    [Fact]
    public void The_panel_still_starts_with_no_Google_credentials_at_all()
    {
        // Deliberate: this product is developed on a machine that has none, and an app that refuses
        // to boot without them cannot serve a single existing download link either.
        using var provider = Build(new Dictionary<string, string?>());
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDriveClient>().Should().NotBeNull();
    }

    private static Dictionary<string, string?> Configured() => new()
    {
        ["Google:ClientId"] = "client-id.apps.googleusercontent.com",
        ["Google:ClientSecret"] = "client-secret",
        ["Google:RedirectUri"] = "https://drive.example/oauth/google",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddGoogleDrive(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
