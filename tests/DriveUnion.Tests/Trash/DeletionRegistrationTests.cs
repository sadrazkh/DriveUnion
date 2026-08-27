using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// The two lines a host adds for deleting a lot at once, and why they are two.
///
/// <para>Without the second one every delete still works and no file ever leaves the folder it was
/// uploaded to — a failure with no symptom on any screen, which is exactly the kind this suite is
/// asked to catch before a deployment does.</para>
/// </summary>
public class DeletionRegistrationTests
{
    [Fact]
    public async Task Both_halves_resolve_from_the_one_registration()
    {
        await using var harness = ServiceTestHarness.Create();

        await using var provider = Provider(harness);
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IDeletionQueue>().Should().BeOfType<DeletionQueue>();
        scope.ServiceProvider.GetRequiredService<IDeletionRunner>().Should().BeOfType<DeletionRunner>();
    }

    [Fact]
    public void The_loop_is_a_separate_line()
    {
        var services = new ServiceCollection();

        services.AddDriveUnionDeletions();

        // Separate for the reason AddDriveUnionTrashSweeper is separate from AddDriveUnionTrash:
        // every in-process test host boots the real pipeline over one shared SQLite connection, and a
        // background loop opening scopes against it turns unrelated suites into «database is locked».
        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));

        services.AddDriveUnionDeletionWorker();

        services.Should().ContainSingle(
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(DeletionWorker));
    }

    private static ServiceProvider Provider(ServiceTestHarness harness)
    {
        var services = new ServiceCollection();

        // A host brings its own; a bare ServiceCollection does not, and the runner logs what it gives
        // up on.
        services.AddLogging();

        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddSingleton<IDriveClient>(harness.Drive);
        services.AddSingleton<IDriveFolders>(harness.TrashFolders());
        services.AddScoped(_ => harness.NewContext());

        services.AddDriveUnionServices();
        services.AddDriveUnionTrash();
        services.AddDriveUnionDeletions();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
