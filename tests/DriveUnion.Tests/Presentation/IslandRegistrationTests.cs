using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// Every mount point a view draws has to have something that mounts on it.
///
/// This test exists because the product shipped without it. <c>Views/Files/Upload.cshtml</c> drew a
/// <c>data-island="upload-panel"</c> with a no-JavaScript fallback inside, <c>main.ts</c> registered
/// no island by that name, and so every visitor to the upload screen was told that uploading needs
/// JavaScript — in a browser that had it. The same was true of <c>copy-link</c>. Nothing failed:
/// the C# compiled, the bundle built, the page rendered, and the one API the product is sold on had
/// no client at all.
///
/// A mount point and its mounter are a contract written in two languages that never meet, so this
/// is the only place the two halves can be compared.
/// </summary>
public class IslandRegistrationTests
{
    /// <summary><c>data-island="name"</c>, single or double quoted, as Razor may write either.</summary>
    private static readonly Regex MountPoint = new(
        """data-island\s*=\s*["']([a-z0-9-]+)["']""",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    /// <summary>A key of the <c>islands</c> record in main.ts: <c>'name': …</c>.</summary>
    private static readonly Regex Registration = new(
        """^\s*'([a-z0-9-]+)'\s*:""",
        RegexOptions.Multiline,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_island_a_view_mounts_is_registered_in_main_ts()
    {
        var mounted = MountPoints();
        var registered = Registrations();

        mounted.Should().NotBeEmpty("the panel has islands, so a run that finds none is a broken test");

        var orphans = mounted.Where(name => !registered.Contains(name)).ToList();

        orphans.Should().BeEmpty(
            "a data-island nothing mounts leaves its server-rendered fallback on the screen for "
            + "ever, which is how the upload screen came to tell people it needed JavaScript they "
            + "already had");
    }

    [Fact]
    public void Every_island_registered_in_main_ts_is_mounted_by_a_view()
    {
        var mounted = MountPoints();
        var registered = Registrations();

        registered.Should().NotBeEmpty();

        var unused = registered.Where(name => !mounted.Contains(name)).ToList();

        // The other direction, and worth having: a registration nobody mounts is dead weight in a
        // bundle every page downloads, and usually means a screen was renamed and its island was not.
        unused.Should().BeEmpty("an island nothing mounts is shipped to every visitor for nothing");
    }

    private static HashSet<string> MountPoints()
    {
        var views = new DirectoryInfo(Path.Combine(RepositoryRoot().FullName, "src", "DriveUnion.Web", "Views"));

        return views
            .EnumerateFiles("*.cshtml", SearchOption.AllDirectories)
            .SelectMany(file => MountPoint.Matches(File.ReadAllText(file.FullName)))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> Registrations()
    {
        var main = Path.Combine(
            RepositoryRoot().FullName, "src", "DriveUnion.Web", "Scripts", "main.ts");

        return Registration
            .Matches(File.ReadAllText(main))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriveUnion.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            $"No DriveUnion.slnx above {AppContext.BaseDirectory}; this test reads the repository's own source.");
    }
}
