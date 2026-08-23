using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DriveUnion.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without booting the web app.
///
/// The connection string here is never connected to — EF only needs a provider to know how to shape
/// the SQL it writes into a migration. Keeping it local to this project means a migration can be
/// generated before anyone has a database, which is the situation this repo is actually in.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DriveUnionDbContext>
{
    public DriveUnionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DriveUnionDbContext>()
            .UseNpgsql("Host=localhost;Database=driveunion_design_time;Username=postgres")
            .Options;

        return new DriveUnionDbContext(options);
    }
}
