using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Settings;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Tests.Trash;

/// <summary>
/// The one line a host adds, and what it is worth.
///
/// <para><c>AddDriveUnionTrash</c> is what turns <c>FileCatalog.DeleteAsync</c> from a column stamp
/// into a move. That dependency is optional on the constructor so the harnesses that build a
/// catalogue by hand are not made to supply a Drive — which is only safe if the container really
/// does hand it the registered one, and that is a claim worth a test rather than a comment.</para>
/// </summary>
public class TrashRegistrationTests
{
    [Fact]
    public async Task The_registered_catalogue_deletes_into_the_trash()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account, sizeBytes: 4096);
        var home = harness.FolderOf(file);

        await using var provider = Provider(harness, withTrash: true);
        await using var scope = provider.CreateAsyncScope();

        var catalogue = scope.ServiceProvider.GetRequiredService<IFileCatalog>();

        (await catalogue.DeleteAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        harness.FolderOf(file).Should().NotBe(home);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.PurgeAfter.Should().NotBeNull();
        row.RestoreFolderId.Should().Be(home);
    }

    [Fact]
    public async Task The_customer_and_sweeper_surfaces_both_resolve()
    {
        await using var harness = ServiceTestHarness.Create();

        await using var provider = Provider(harness, withTrash: true);
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ITrash>().Should().BeOfType<TrashService>();
        scope.ServiceProvider.GetRequiredService<ITrashPurge>().Should().BeOfType<TrashPurge>();
        scope.ServiceProvider.GetRequiredService<IOperatorSettingsStore>()
            .Should().BeOfType<OperatorSettingsStore>();
    }

    [Fact]
    public async Task Without_the_line_the_catalogue_still_resolves_and_still_soft_deletes()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = await harness.SeedUploadedFileAsync(tenant, account);

        await using var provider = Provider(harness, withTrash: false);
        await using var scope = provider.CreateAsyncScope();

        // A host that has not added the trash is not a host that fails to start. It is one where a
        // delete is what it was before this slice: the row stamped, the links revoked, and no move.
        (await scope.ServiceProvider.GetRequiredService<IFileCatalog>()
            .DeleteAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        harness.Drive.Calls.Should().NotContain(c => c.Operation == Fakes.FakeDriveOperation.Move);

        var row = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);

        row.DeletedAt.Should().NotBeNull();
        row.PurgeAfter.Should().BeNull();
    }

    [Fact]
    public void The_sweeper_loop_is_a_separate_line()
    {
        var services = new ServiceCollection();

        services.AddDriveUnionTrash();

        // Separate for the reason AddDriveUnionTelegramTransport is separate: every in-process test
        // host boots the real pipeline over one shared SQLite connection, and a background loop
        // opening scopes against it turns unrelated suites into "database is locked".
        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));

        services.AddDriveUnionTrashSweeper();

        services.Should().ContainSingle(
            d => d.ServiceType == typeof(IHostedService)
                 && d.ImplementationType == typeof(TrashPurgeService));
    }

    private static ServiceProvider Provider(ServiceTestHarness harness, bool withTrash)
    {
        var services = new ServiceCollection();

        // A host brings its own; a bare ServiceCollection does not, and the trash logs what it
        // destroys.
        services.AddLogging();

        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddSingleton<IDriveClient>(harness.Drive);
        services.AddSingleton<IDriveFolders>(harness.TrashFolders());
        services.AddScoped(_ => harness.NewContext());

        services.AddDriveUnionServices();

        if (withTrash) services.AddDriveUnionTrash();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
