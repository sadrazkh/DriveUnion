using DriveUnion.Infrastructure.Persistence;
using FluentAssertions;

namespace DriveUnion.Tests.Persistence;

/// <summary>
/// Where `dotnet ef` gets its connection string.
///
/// These exist because of a real morning lost to one message: `Update-Database` answered
/// "No password has been provided but the backend requires one", against a database called
/// driveunion_design_time that nobody had ever asked for. The placeholder was a plausible-looking
/// localhost string, so EF tried it, and Npgsql's complaint said nothing about what to set.
/// </summary>
public class DesignTimeConnectionTests
{
    private const string Real = "Host=localhost;Port=5432;Database=driveunion;Username=postgres;Password=s3cret";
    private const string Other = "Host=elsewhere;Database=driveunion;Username=postgres;Password=other";

    [Fact]
    public void The_environment_variable_wins()
    {
        DesignTimeConnection.Resolve(variableValue: Real, configuredValue: Other).Should().Be(Real);
    }

    [Fact]
    public void Configuration_is_used_when_the_variable_is_not_set()
    {
        DesignTimeConnection.Resolve(variableValue: null, configuredValue: Real).Should().Be(Real);
        DesignTimeConnection.Resolve(variableValue: "   ", configuredValue: Real).Should().Be(Real);
    }

    [Fact]
    public void With_neither_it_falls_back_so_a_migration_can_still_be_written()
    {
        // `dotnet ef migrations add` needs a provider, not a server. Refusing here would make the
        // schema unwritable on a machine that has no database — which is how this one started.
        DesignTimeConnection.Resolve(null, null).Should().Be(DesignTimeConnection.OfflinePlaceholder);
    }

    [Fact]
    public void The_fallback_cannot_be_mistaken_for_a_server()
    {
        // .invalid is reserved by RFC 6761 and never resolves, so an `ef database update` that
        // reaches this string fails saying the host does not exist — a sentence that points at the
        // configuration — instead of asking for a password against a real listener on localhost.
        DesignTimeConnection.OfflinePlaceholder.Should().Contain(".invalid");
        DesignTimeConnection.OfflinePlaceholder.Should().NotContain("localhost");
        DesignTimeConnection.OfflinePlaceholder.Should().NotContain("Password=");
    }

    [Fact]
    public void The_names_it_reads_are_the_ones_the_app_and_the_docs_use()
    {
        // A drift here is silent: the factory would quietly fall back and the operator would be
        // told about a password rather than about a key nobody sets any more.
        DesignTimeConnection.Variable.Should().Be("DRIVEUNION_CONNECTION");
        DesignTimeConnection.ConfigurationKey.Should().Be("ConnectionStrings:Default");
    }
}
