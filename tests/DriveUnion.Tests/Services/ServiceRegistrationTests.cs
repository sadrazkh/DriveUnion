using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Services;

/// <summary>
/// What <c>AddDriveUnionServices</c> hands back when something actually asks it.
///
/// <para>The upload coordinator has two constructors — one of them is the shape harnesses outside
/// this slice build by hand — and the container picking the wrong one would be a folder cache per
/// request instead of per process: correct, silent, and paying for a pair of Drive calls on every
/// upload in the product.</para>
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void The_upload_coordinator_is_built_with_the_registered_folder_resolver()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IDriveClient>(new FakeDriveClient());
        services.AddDriveUnionServices();

        // validateScopes, because the folder cache is the one singleton here and a singleton that
        // captured a scoped database context would be a request's context living for ever.
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<IUploadCoordinator>().Should().BeOfType<UploadCoordinator>();
        first.ServiceProvider.GetRequiredService<IDriveFolders>().Should().BeOfType<DriveFolders>();

        first.ServiceProvider.GetRequiredService<DriveFolderCache>()
            .Should().BeSameAs(
                second.ServiceProvider.GetRequiredService<DriveFolderCache>(),
                "two requests that resolve the same folder must be able to share the answer");

        first.ServiceProvider.GetRequiredService<IDriveFolders>()
            .Should().NotBeSameAs(
                second.ServiceProvider.GetRequiredService<IDriveFolders>(),
                "the resolver itself reads through the request's own context");
    }
}
