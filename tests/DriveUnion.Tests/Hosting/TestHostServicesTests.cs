using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Hosting;

/// <summary>
/// Every in-process host in this suite takes the background loops out.
///
/// <para>The rule is written down in <see cref="TestHostServices.RemoveEveryBackgroundLoop"/>; this
/// is what makes it a rule rather than a habit. A harness that skips it does not fail — it makes
/// <i>somebody else</i> fail, intermittently, in a stack that names neither the loop nor the file
/// that let it run. Four harnesses were written before anyone connected the two, and three of the
/// four had a filtered version of this removal that named the loops its author happened to know
/// about, which is how the trash sweeper survived in the Telegram harness and the Telegram drainer
/// survived in the trash one.</para>
///
/// <para>Nothing here starts a host, so this test is fast and honest about what it can see: it reads
/// the source. A harness that calls the method under a name this regex does not match is a harness
/// this test cannot judge — which is the reason the method has one name and no overloads.</para>
/// </summary>
public class TestHostServicesTests
{
    [Fact]
    public void Every_in_process_host_removes_the_background_loops()
    {
        var exposed = new List<string>();
        var hosts = 0;

        foreach (var file in TestSources())
        {
            var source = File.ReadAllText(file);

            // The base class is the definition of "starts the real pipeline in this process".
            if (!source.Contains("WebApplicationFactory<Program>", StringComparison.Ordinal)) continue;

            hosts++;

            if (!source.Contains("RemoveEveryBackgroundLoop", StringComparison.Ordinal))
            {
                exposed.Add(Path.GetFileName(file));
            }
        }

        hosts.Should().BeGreaterThan(5, "the suite has a host harness per area; finding none is a broken test");

        exposed.Should().BeEmpty(
            "a host that leaves Program.cs's loops running opens scopes against another harness's "
            + "SQLite connection while that harness is disposing it, and the NullReferenceException "
            + "surfaces in whichever unrelated test was tearing down at the time");
    }

    [Fact]
    public void No_host_keeps_a_filtered_copy_of_the_removal()
    {
        // The shape that let the defect survive three fixes: a removal that matches on an
        // implementation namespace covers the loops that existed when it was written and silently
        // stops covering the next one. There is one place that decides this now.
        //
        // Scoped to files that start a host, and that is the whole distinction. TrashRegistrationTests,
        // LocalDiskRegistrationTests and GoogleServiceCollectionExtensionsTests all name
        // IHostedService too — to assert a loop *is* registered — and they read the service
        // collection without ever building a host, so nothing they do can race anybody's connection.
        // They are the tests that catch a missing AddHostedService, and this rule must not reach them.
        var byHand = new Regex(
            @"ServiceType\s*==\s*typeof\(IHostedService\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var offenders = TestSources()
            .Select(file => (file, source: File.ReadAllText(file)))
            .Where(f => f.source.Contains("WebApplicationFactory<Program>", StringComparison.Ordinal))
            .Where(f => byHand.IsMatch(f.source))
            .Select(f => Path.GetFileName(f.file))
            .ToList();

        offenders.Should().BeEmpty(
            "a host that removes hosted services by hand removes the ones its author knew about, "
            + "which is how the trash sweeper survived in the Telegram harness and the Telegram "
            + "drainer survived in the trash one");
    }

    private static IEnumerable<string> TestSources() =>
        Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string TestsDirectory() =>
        Path.Combine(RepositoryRoot().FullName, "tests", "DriveUnion.Tests");

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriveUnion.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            $"No DriveUnion.slnx above {AppContext.BaseDirectory}; this test reads the suite's own source.");
    }
}
