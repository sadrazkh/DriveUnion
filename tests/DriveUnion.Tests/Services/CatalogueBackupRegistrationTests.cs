using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Backup;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The two lines a host adds for catalogue backups, and why they are two.
///
/// <para>The split is the same one the trash sweeper, the migration worker and the Telegram drainer
/// make: every in-process test host boots the real pipeline over one shared SQLite connection, and a
/// background loop opening scopes against it turns unrelated suites into «database is locked». So a
/// host has to be able to have the writer without the loop — and if the second line is ever dropped
/// from <c>Program.cs</c>, the screen still draws, the button still queues a row, and no snapshot is
/// ever written. That is the quiet failure this feature exists to prevent, so it gets a test rather
/// than a comment.</para>
/// </summary>
public class CatalogueBackupRegistrationTests
{
    private static ServiceCollection Services(SqliteConnection connection)
    {
        var services = new ServiceCollection();

        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IDriveClient>(new FakeDriveClient());

        // The writer logs, and a host always has logging — a web application builder registers it
        // before anything else. Added by hand here because a bare ServiceCollection does not.
        services.AddLogging();
        services.AddDriveUnionServices();

        return services;
    }

    [Fact]
    public void Both_halves_resolve_from_the_container()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = Services(connection);
        services.AddDriveUnionCatalogueBackup();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        // The writer takes an optional chunk size that nothing in the product passes. A container
        // that could not build a constructor with a defaulted parameter would fail here rather than
        // at four in the morning on the first scheduled run.
        scope.ServiceProvider.GetRequiredService<ICatalogueBackup>().Should().BeOfType<CatalogueBackup>();
        scope.ServiceProvider.GetRequiredService<ICatalogueSnapshots>().Should().BeOfType<CatalogueSnapshots>();
    }

    [Fact]
    public void The_writer_comes_without_a_loop_and_the_loop_is_a_second_line()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = Services(connection);
        services.AddDriveUnionCatalogueBackup();

        services.Count(d => d.ServiceType == typeof(IHostedService))
            .Should().Be(0, "a test host must be able to have the writer without the background loop");

        services.AddDriveUnionCatalogueBackupWorker();

        services.Count(d => d.ServiceType == typeof(IHostedService))
            .Should().Be(1, "and Program.cs's second line is what actually writes snapshots");
    }

    [Fact]
    public void A_chunk_size_Drive_would_stall_on_is_refused_at_construction()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = Services(connection);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        var drive = scope.ServiceProvider.GetRequiredService<IDriveClient>();
        var folders = scope.ServiceProvider.GetRequiredService<IDriveFolders>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        // Drive does not reject a chunk that is not a multiple of 256 KiB — it accepts the request
        // and quietly stops acknowledging bytes, which on the wire is a stalled upload with no error
        // anywhere. Refusing it here makes it a constructor argument instead of a support ticket.
        var build = () => new CatalogueBackup(
            db,
            drive,
            folders,
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogueBackup>.Instance,
            chunkSize: 300_000);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
