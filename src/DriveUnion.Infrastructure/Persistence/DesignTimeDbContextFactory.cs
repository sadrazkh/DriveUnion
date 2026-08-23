using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DriveUnion.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without booting the web app.
///
/// Writing a migration needs no database at all — EF only needs a provider to know what shape of SQL
/// to emit — so the fallback below is a placeholder that is never connected to. That is what let the
/// schema be written before anyone had a server to point at.
///
/// <c>DRIVEUNION_CONNECTION</c> is for the other direction: <c>dotnet ef database update</c>, which
/// does need a real server. It is an environment variable rather than an argument so the password
/// never lands in a shell history or a build log.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DriveUnionDbContext>
{
    public const string ConnectionVariable = "DRIVEUNION_CONNECTION";

    public DriveUnionDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            connection = "Host=localhost;Database=driveunion_design_time;Username=postgres";
        }

        var options = new DbContextOptionsBuilder<DriveUnionDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new DriveUnionDbContext(options);
    }
}
