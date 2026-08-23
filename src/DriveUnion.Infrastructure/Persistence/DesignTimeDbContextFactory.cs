using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace DriveUnion.Infrastructure.Persistence;

/// <summary>
/// Where <c>dotnet ef</c> and Visual Studio's <c>Update-Database</c> get their connection string.
///
/// Two commands with opposite needs run through here. <c>migrations add</c> never opens a socket —
/// EF only wants a provider so it knows what SQL to emit — which is why a migration could be
/// written here before anyone had a server. <c>database update</c> needs a real one.
///
/// The first version served both with a plausible-looking <c>Host=localhost;…;Username=postgres</c>
/// and no password. That is the worst of the three options: EF happily tried it, and Npgsql answered
/// "No password has been provided but the backend requires one" against a database called
/// <c>driveunion_design_time</c> that nobody had asked for. The message named a password when the
/// missing thing was configuration.
/// </summary>
public static class DesignTimeConnection
{
    /// <summary>Set this to point a migration at a specific server, ahead of everything else.</summary>
    public const string Variable = "DRIVEUNION_CONNECTION";

    /// <summary>The same key the panel itself reads, so one setting serves both.</summary>
    public const string ConfigurationKey = "ConnectionStrings:Default";

    /// <summary>
    /// Used only when nothing is configured, and deliberately unreachable. <c>.invalid</c> is
    /// reserved by RFC 6761 and never resolves, so a <c>database update</c> that gets this far fails
    /// saying the host does not exist — which sends the reader to their configuration — rather than
    /// asking for a password against a real listener on localhost.
    /// </summary>
    public const string OfflinePlaceholder =
        "Host=design-time-only.invalid;Database=driveunion;Username=design-time";

    public static string Resolve(string? variableValue, string? configuredValue)
    {
        if (!string.IsNullOrWhiteSpace(variableValue)) return variableValue;
        if (!string.IsNullOrWhiteSpace(configuredValue)) return configuredValue;
        return OfflinePlaceholder;
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DriveUnionDbContext>
{
    /// <summary>
    /// DriveUnion.Web's <c>UserSecretsId</c>. Naming it here is the price of the panel's Postgres
    /// password living in user-secrets rather than in a file: EF prefers a design-time factory over
    /// the startup project's host, so <c>--startup-project DriveUnion.Web</c> does not bring that
    /// configuration with it. Reading the same store means the migration and the running panel
    /// cannot end up pointed at different databases.
    /// </summary>
    private const string WebUserSecretsId = "e40bef44-41b0-42f8-b5ca-cb425fd40bb7";

    public DriveUnionDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            // The string overload treats a missing store as optional, which is what a machine that
            // has never run `user-secrets set` needs.
            .AddUserSecrets(WebUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var connection = DesignTimeConnection.Resolve(
            Environment.GetEnvironmentVariable(DesignTimeConnection.Variable),
            configuration[DesignTimeConnection.ConfigurationKey]);

        var options = new DbContextOptionsBuilder<DriveUnionDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new DriveUnionDbContext(options);
    }
}
