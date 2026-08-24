using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// Every mount point a view draws has to have something that mounts on it, and it has to be drawn
/// where the thing that mounts it expects to find it.
///
/// This test exists because the product shipped without the first half. <c>Views/Files/Upload.cshtml</c>
/// drew a <c>data-island="upload-panel"</c> with a no-JavaScript fallback inside, <c>main.ts</c>
/// registered no island by that name, and so every visitor to the upload screen was told that
/// uploading needs JavaScript — in a browser that had it. The same was true of <c>copy-link</c>.
/// Nothing failed: the C# compiled, the bundle built, the page rendered, and the one API the product
/// is sold on had no client at all.
///
/// The second half arrived with navigate.ts. Navigation swaps <c>main.app-content</c> and leaves the
/// shell above it standing, so an island now lives one of two lives: mounted and unmounted once per
/// navigation, or mounted once and never touched. Which one it gets is decided by where a view puts
/// the mount point, and what it was written for is declared in main.ts. Those two can disagree — an
/// island written to be mounted once, drawn inside the swapped region, is a Vue app leaked on every
/// navigation — and nothing about that disagreement fails a build.
///
/// A mount point and its mounter are a contract written in two languages that never meet, so this is
/// the only place the two halves can be compared.
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

    /// <summary>The <c>region:</c> an island declares for itself.</summary>
    private static readonly Regex Region = new(
        """region\s*:\s*'(content|shell)'""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>The selector navigate.ts replaces, read from the file rather than repeated here.</summary>
    private static readonly Regex ContentSelector = new(
        """const ContentSelector = '([^']+)';""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>A view sets its own layout, or takes _ViewStart's.</summary>
    private static readonly Regex DeclaredLayout = new(
        """Layout\s*=\s*(?:"([^"]+)"|(null))""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>Where an island is drawn, and whether a navigation replaces it.</summary>
    private sealed record Placement(string Name, string File, bool InsideSwappedRegion);

    /// <summary>The character range of one file that a navigation replaces.</summary>
    private readonly record struct Swapped(int Start, int End)
    {
        internal static Swapped Nothing => new(0, 0);

        internal static Swapped Everything => new(0, int.MaxValue);

        internal bool Contains(int index) => index >= Start && index < End;
    }

    [Fact]
    public void Every_island_a_view_mounts_is_registered_in_main_ts()
    {
        var mounted = MountedNames();
        var registered = Registrations().Keys.ToHashSet(StringComparer.Ordinal);

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
        var mounted = MountedNames();
        var registered = Registrations().Keys.ToHashSet(StringComparer.Ordinal);

        registered.Should().NotBeEmpty();

        var unused = registered.Where(name => !mounted.Contains(name)).ToList();

        // The other direction, and worth having: a registration nobody mounts is dead weight in a
        // bundle every page downloads, and usually means a screen was renamed and its island was not.
        unused.Should().BeEmpty("an island nothing mounts is shipped to every visitor for nothing");
    }

    [Fact]
    public void Every_island_declares_which_side_of_the_swap_it_lives_on()
    {
        var undeclared = Registrations()
            .Where(entry => entry.Value is null)
            .Select(entry => entry.Key)
            .ToList();

        // The declaration is the only thing that says what an island was written to survive. Without
        // it the test below cannot tell a shell island drawn in the wrong place from a content island
        // drawn in the right one, and the author of the next island has nothing to copy.
        undeclared.Should().BeEmpty("an island that does not say where it lives cannot be checked");
    }

    [Fact]
    public void Shell_islands_are_drawn_outside_the_swapped_region_and_content_islands_inside()
    {
        var registered = Registrations();

        var wrong = Placements()
            .Where(placement => registered.TryGetValue(placement.Name, out var region)
                && region is not null
                && (region == "content") != placement.InsideSwappedRegion)
            .Select(placement => $"{placement.Name} in {placement.File}")
            .ToList();

        wrong.Should().BeEmpty(
            "a 'shell' island drawn inside main.app-content is mounted again by every navigation "
            + "and unmounted by none of them, and a 'content' island drawn in the shell is mounted "
            + "once and then shows the first page it saw for the rest of the session — neither of "
            + "which fails a build, and only one of which is visible");
    }

    [Fact]
    public void The_layout_still_carries_the_element_a_navigation_replaces()
    {
        var navigate = File.ReadAllText(Path.Combine(ScriptsDirectory(), "navigate.ts"));

        var selector = ContentSelector.Match(navigate);
        selector.Success.Should().BeTrue("navigate.ts declares the element it swaps in one place");

        var parts = selector.Groups[1].Value.Split('.');
        parts.Should().HaveCount(2, "the selector is one tag and one class, so this test can look for it");

        var layout = File.ReadAllText(Path.Combine(ViewsDirectory(), "Shared", "_Layout.cshtml"));
        var element = ElementWithClass(parts[0], parts[1]).Matches(layout);

        // Renaming the class in Razor breaks nothing loudly: the C# compiles, the bundle builds, and
        // every link in the panel quietly goes back to a full page load — taking the upload queue
        // with it on each one, which is the whole thing this architecture bought.
        element.Should().ContainSingle(
            $"_Layout.cshtml is where <{parts[0]} class=\"{parts[1]}\"> lives and navigate.ts "
            + "replaces exactly one of them");
    }

    private static HashSet<string> MountedNames() =>
        Placements().Select(placement => placement.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every mount point in every view, and whether a navigation replaces it.</summary>
    private static List<Placement> Placements()
    {
        var views = new DirectoryInfo(ViewsDirectory());
        var areas = new DirectoryInfo(Path.Combine(RepositoryRoot().FullName, "src", "DriveUnion.Web", "Areas"));

        var files = views.EnumerateFiles("*.cshtml", SearchOption.AllDirectories);

        if (areas.Exists)
        {
            files = files.Concat(areas.EnumerateFiles("*.cshtml", SearchOption.AllDirectories));
        }

        return files
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file.FullName);
                var swapped = SwappedSpan(text);

                return MountPoint.Matches(text).Select(match => new Placement(
                    match.Groups[1].Value,
                    file.Name,
                    swapped.Contains(match.Index)));
            })
            .ToList();
    }

    /// <summary>
    /// The character range of this file that a navigation replaces.
    ///
    /// A layout carries the swapped element itself, so the answer is the span between its tags. Any
    /// other view is rendered into whatever layout it names, and everything in it is inside that
    /// layout's <c>@RenderBody()</c> — so the question becomes which layout, and the two that are not
    /// the panel's own (the chrome-less public page, and the OAuth popup) are never swapped at all.
    /// </summary>
    private static Swapped SwappedSpan(string text)
    {
        if (text.Contains("RenderBody()", StringComparison.Ordinal))
        {
            var opening = ElementWithClass("main", "app-content").Match(text);
            if (!opening.Success) return Swapped.Nothing;

            var start = opening.Index + opening.Length;
            var end = text.IndexOf("</main>", start, StringComparison.OrdinalIgnoreCase);

            return end < 0 ? Swapped.Nothing : new Swapped(start, end);
        }

        var declared = DeclaredLayout.Match(text);
        var layout = declared.Success ? declared.Groups[1].Value : "_Layout";

        return layout == "_Layout" ? Swapped.Everything : Swapped.Nothing;
    }

    /// <summary>
    /// The islands main.ts registers, each with the region it declares — or null where it declares
    /// none. Regions are read from the block between one registration and the next, which is the
    /// only structure a record literal has to offer.
    /// </summary>
    private static Dictionary<string, string?> Registrations()
    {
        var main = File.ReadAllText(Path.Combine(ScriptsDirectory(), "main.ts"));
        var entries = Registration.Matches(main);
        var registrations = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var start = entries[i].Index + entries[i].Length;
            var end = i + 1 < entries.Count ? entries[i + 1].Index : main.Length;
            var region = Region.Match(main, start, end - start);

            registrations[entries[i].Groups[1].Value] = region.Success ? region.Groups[1].Value : null;
        }

        return registrations;
    }

    /// <summary><c>&lt;tag … class="… wanted …"&gt;</c>, in Razor, where the class list may hold anything else.</summary>
    private static Regex ElementWithClass(string tag, string wanted) => new(
        $"""<{Regex.Escape(tag)}\b[^>]*class\s*=\s*"[^"]*\b{Regex.Escape(wanted)}\b[^"]*"[^>]*>""",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    private static string ViewsDirectory() =>
        Path.Combine(RepositoryRoot().FullName, "src", "DriveUnion.Web", "Views");

    private static string ScriptsDirectory() =>
        Path.Combine(RepositoryRoot().FullName, "src", "DriveUnion.Web", "Scripts");

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
