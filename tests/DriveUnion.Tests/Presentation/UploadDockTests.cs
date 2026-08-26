using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Links;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The upload dock, and the half of it a test without a browser can hold.
///
/// What the dock is for is a runtime fact: a transfer that keeps moving while somebody walks around
/// the panel. Nothing here can watch that happen — there is no browser in this suite and no
/// JavaScript runner — so this file holds the three things that decided it and that a later edit
/// could quietly undo:
///
/// <list type="bullet">
///   <item>where the mount point is, because a dock inside <c>main.app-content</c> is swapped away
///   by the next navigation along with the progress it was drawing;</item>
///   <item>who is shown it, because the queue's configuration is behind a workspace and a dock
///   without one is a control with nothing to control;</item>
///   <item>that the two views own no queue of their own, which is the defect the shared store
///   replaced — the upload screen used to hold the items and run the pump, so leaving the screen
///   ended the upload.</item>
/// </list>
///
/// The parts that are deliberately not asserted here are named where they would have gone.
/// </summary>
public class UploadDockTests
{
    private const string Layout = "src/DriveUnion.Web/Views/Shared/_Layout.cshtml";
    private const string Dock = "src/DriveUnion.Web/Scripts/islands/UploadDock.vue";
    private const string Panel = "src/DriveUnion.Web/Scripts/islands/UploadPanel.vue";

    private const string MountPoint = @"data-island=""upload-dock""";

    // ------------------------------------------------------------------ where it is mounted

    /// <summary>
    /// The dock is outside the box navigation replaces.
    ///
    /// <c>main.app-content</c> is what a swap writes over. A dock rendered in there is re-created on
    /// every navigation — which is the one thing it must not be, because the queue it draws is
    /// alive and the component that draws it would be torn down mid-chunk.
    /// </summary>
    [Fact]
    public void The_dock_is_mounted_outside_the_content_a_navigation_replaces()
    {
        var source = Read(Layout);

        var opens = source.IndexOf(@"<main class=""app-content"">", StringComparison.Ordinal);
        var closes = source.IndexOf("</main>", StringComparison.Ordinal);

        opens.Should().BePositive("the shell renders the content column as <main class=\"app-content\">");
        closes.Should().BeGreaterThan(opens);

        var mount = source.IndexOf(MountPoint, StringComparison.Ordinal);

        mount.Should().BePositive("the shell is where the dock is mounted");
        mount.Should().NotBeInRange(
            opens,
            closes,
            "a dock inside main.app-content is replaced by the next navigation, along with the "
            + "transfer it was reporting on");
    }

    /// <summary>
    /// …and it is mounted once, by the shell, rather than by a screen.
    ///
    /// Two docks would be two views onto one queue drawn on top of each other in the same corner.
    /// </summary>
    [Fact]
    public void The_dock_is_mounted_by_the_shell_and_by_nothing_else()
    {
        var views = new DirectoryInfo(Path.Combine(Root().FullName, "src", "DriveUnion.Web", "Views"));

        var mounting = views
            .EnumerateFiles("*.cshtml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file.FullName).Contains(MountPoint, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Root().FullName, file.FullName).Replace('\\', '/'))
            .ToList();

        mounting.Should().Equal([Layout]);
    }

    /// <summary>
    /// The dock and the configuration it runs on are behind the same question.
    ///
    /// <c>data-upload-config</c> carries the begin URL and the antiforgery token, and it is rendered
    /// only for a caller with a workspace. A dock outside that block would mount for the sign-in
    /// page too and read its configuration off an element that is not there.
    /// </summary>
    [Fact]
    public void The_dock_is_behind_the_same_workspace_question_as_its_configuration()
    {
        var source = Read(Layout);

        var config = source.IndexOf("data-upload-config", StringComparison.Ordinal);
        config.Should().BePositive();

        var condition = source.LastIndexOf("@if (hasWorkspace)", config, StringComparison.Ordinal);
        condition.Should().BePositive("the configuration is rendered inside an @if (hasWorkspace)");

        var block = BlockAfter(source, condition);

        source.IndexOf(MountPoint, StringComparison.Ordinal).Should().BeInRange(
            block.Start,
            block.End,
            "the dock has to be absent for a caller with no workspace, the same way its "
            + "configuration is");
    }

    // ------------------------------------------------------------------ who is shown it

    [Fact]
    public async Task A_caller_with_a_workspace_is_given_the_dock_and_its_configuration()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        var markup = await harness.NewClient(tenant.Id).GetStringAsync(new Uri("/files", UriKind.Relative));

        markup.Should().Contain(MountPoint);
        markup.Should().Contain("data-upload-config");
    }

    /// <summary>
    /// The other half, on the one panel-shelled page an anonymous caller can reach.
    ///
    /// Every other route challenges, so the sign-in address is the only place this can be asked —
    /// and it is exactly where the question matters, because that page wears the same layout.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_workspace_is_given_neither()
    {
        using var harness = new PanelPageHarness();
        using var client = harness.NewClient(null);

        using var response = await client.GetAsync(new Uri("/Identity/Account/Login", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var markup = await response.Content.ReadAsStringAsync();

        markup.Should().Contain("app-shell", "the sign-in page wears the panel's own layout");
        markup.Should().NotContain(MountPoint);
        markup.Should().NotContain("data-upload-config");
    }

    // ------------------------------------------------------------------ the corner it sits in

    /// <summary>
    /// The mount point costs nothing while the queue is empty.
    ///
    /// It is a child of <c>.app-shell</c>, which is a flex container, so an ordinary empty element
    /// there is a flex item — a column of nothing between the sidebar and the content on every page
    /// in the panel. <c>display: contents</c> is what makes «no upload running» draw nothing at all
    /// rather than draw an empty box.
    /// </summary>
    [Fact]
    public void The_mount_point_generates_no_box_of_its_own()
    {
        Rule(AppCss(), @"[data-island=""upload-dock""]").Value("display").Should().Be("contents");

        // The island's root is conditional, which is the other half of the same promise. Whether
        // the condition is the right one is a runtime fact and is not asserted here.
        var root = Regex.Match(
            TemplateOf(Read(Dock)),
            @"\A(?:\s*<!--.*?-->)*\s*<\w+(?<attrs>[^>]*)>",
            RegexOptions.Singleline,
            Timeout);

        root.Success.Should().BeTrue("the dock's template opens with an element");

        root.Groups["attrs"].Value.Should().Contain(
            "v-if=",
            "the dock draws nothing at all when there is nothing queued — a permanent empty box in "
            + "the corner of every page is furniture nobody asked for");
    }

    /// <summary>
    /// The dock is layered over the header and under the drawers.
    ///
    /// Both halves are defects if they go: behind the sticky header the dock disappears whenever the
    /// page is scrolled to the top, and over the navigation scrim it is a lit box floating on the
    /// page the scrim exists to push away.
    /// </summary>
    [Fact]
    public void The_dock_is_layered_over_the_header_and_under_the_drawers()
    {
        var css = AppCss();

        var dock = Layer(css, ".upload-dock");

        dock.Should().BeGreaterThan(Layer(css, ".app-header"));
        dock.Should().BeLessThan(Layer(css, @".app-shell[data-nav=""open""] .nav-scrim"));
        dock.Should().BeLessThan(Layer(css, @".app-shell[data-nav] .app-sidebar"));

        // Fixed, so the corner is the window's rather than the scroll position's. The inline and
        // block insets are logical, which PanelLayoutTests already refuses a physical spelling of.
        Rule(css, ".upload-dock").Value("position").Should().Be("fixed");
    }

    // ------------------------------------------------------------------ two views, one queue

    /// <summary>
    /// Neither view owns a queue, and this is the whole of the change.
    ///
    /// <c>UploadPanel.vue</c> used to hold the items, run the pump and send the chunks. That is why
    /// an upload ended when somebody clicked «فایل‌ها»: the page that owned the transfer was the
    /// page being replaced. Both views now read <c>uploads/store.ts</c>, which lives above the
    /// content a navigation swaps, and a second copy of any of this machinery in either of them
    /// would restore the defect without failing anything else.
    /// </summary>
    [Theory]
    [InlineData(Dock)]
    [InlineData(Panel)]
    public void An_upload_view_reads_the_shared_queue_and_does_not_run_one(string island)
    {
        var source = Read(island);

        source.Should().Contain("../uploads/store", $"{island} is a view onto the shared queue");

        foreach (var machinery in new[] { "XMLHttpRequest", "new AbortController", "fetch(", "function pump" })
        {
            source.Should().NotContain(
                machinery,
                "{0} would be sending its own bytes, and a queue a page owns is a queue that page ends",
                island);
        }
    }

    /// <summary>
    /// The language comes from the same element the store reads it from.
    ///
    /// <c>document.documentElement.lang</c> is the tempting second source, and it is the one that
    /// goes stale: the queue's configuration is re-rendered by every response, and the document's
    /// attribute belongs to whichever response happened to be a full page load.
    /// </summary>
    [Theory]
    [InlineData(Dock)]
    [InlineData(Panel)]
    public void An_upload_view_takes_its_language_from_the_queues_configuration(string island)
    {
        var source = Read(island);

        source.Should().Contain("config().lang");
        source.Should().NotContain("documentElement");
    }

    /// <summary>
    /// How many files go at once is offered from the store's own list.
    ///
    /// A second list written here would be a set of choices the pump has never heard of: it reads
    /// <c>ConcurrencyChoices</c> when it validates what it is given, so an option outside that list
    /// silently becomes the default.
    /// </summary>
    [Fact]
    public void The_upload_screen_offers_the_stores_own_concurrency_choices()
    {
        var source = Read(Panel);

        source.Should().Contain("ConcurrencyChoices");

        Regex.IsMatch(source, @"\[\s*\d+\s*,\s*\d+", RegexOptions.None, Timeout)
            .Should().BeFalse("the choices are the store's list, not a copy of its contents");
    }

    /// <summary>
    /// The screen says what an upload cannot survive, in both languages.
    ///
    /// A transfer outlives navigation inside the panel and outlives nothing else: the File handle
    /// belongs to the page that opened it, so closing or reloading the tab ends it. Somebody who
    /// assumes otherwise finds out with 90 GB sent.
    /// </summary>
    [Fact]
    public void The_upload_screen_says_what_a_reload_costs()
    {
        var source = Read(Panel);

        Regex.Matches(source, "tabWarning:", RegexOptions.None, Timeout)
            .Count.Should().Be(2, "the sentence exists in the Persian panel and in the English one");

        TemplateOf(source).Should().Contain(
            "text().tabWarning",
            "a sentence nothing renders is a sentence nobody is told");
    }

    // ------------------------------------------------------------------ direction

    /// <summary>
    /// Every byte figure and every percentage in the two views carries its own direction.
    ///
    /// In an RTL box the bidi algorithm resolves the space between a European number and a Latin
    /// unit as right-to-left, so «0 B / 202 MB» is laid out «0 MB 202 / B». That is the shape the
    /// first uploader shipped, and the fix has to be on the run itself: <c>dir="ltr"</c> on an
    /// ancestor would take a Persian sentence with it.
    ///
    /// This reads the templates rather than a rendering, because these two screens are drawn by
    /// Vue and there is no browser here to draw them. It sees a figure that is written directly
    /// inside an element; a figure interpolated into a longer expression, or one built in script
    /// and returned as a whole string, is invisible to it.
    /// </summary>
    [Theory]
    [InlineData(Dock)]
    [InlineData(Panel)]
    public void A_figure_in_an_upload_view_carries_its_own_direction(string island)
    {
        var template = TemplateOf(Read(island));

        var undirected = new List<string>();

        foreach (Match figure in Regex.Matches(
            template,
            @"bytes\(|eta\(|Math\.round\(|\.length",
            RegexOptions.None,
            Timeout))
        {
            var tag = ElementAround(template, figure.Index);

            // Null means the match is inside a tag rather than in its text — an :aria-valuenow or a
            // v-if, which is script and not something a reader is laid out.
            if (tag is null) continue;

            if (!tag.Contains(@"dir=""ltr""", StringComparison.Ordinal)) undirected.Add(tag.Trim());
        }

        undirected.Should().BeEmpty(
            "{0} draws these figures in an element that does not state its direction",
            island);
    }

    /// <summary>
    /// …and no isolate is wrapped around prose.
    ///
    /// The mirror of the rule above and the easier mistake to make while fixing it: an ltr isolate
    /// around a Persian sentence lays the sentence out backwards, which is a worse defect than the
    /// one it was reached for.
    /// </summary>
    [Theory]
    [InlineData(Dock)]
    [InlineData(Panel)]
    public void An_isolate_in_an_upload_view_holds_no_persian(string island)
    {
        var template = TemplateOf(Read(island));

        var isolates = Regex
            .Matches(template, @"dir=""ltr""[^>]*>(?<text>[^<]*)<", RegexOptions.None, Timeout)
            .Select(m => m.Groups["text"].Value)
            .ToList();

        isolates.Should().NotBeEmpty("{0} draws Latin readouts, so it has isolates to check", island);

        foreach (var text in isolates)
        {
            Regex.IsMatch(text, PersianCharacter, RegexOptions.None, Timeout).Should().BeFalse(
                "«{0}» in {1} is Persian inside an ltr isolate, which lays it out backwards",
                text.Trim(),
                island);
        }
    }

    // ------------------------------------------------------------------ reading the source

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.
    /// Written as escapes rather than as characters, because the last of the three is invisible.
    /// </summary>
    private const string PersianCharacter = "[\u0600-\u06FF\u200C]";

    /// <summary>The <c>&lt;template&gt;</c> half of a single-file component.</summary>
    private static string TemplateOf(string component)
    {
        var match = Regex.Match(component, @"<template>(?<body>.*)</template>", RegexOptions.Singleline, Timeout);

        Assert.True(match.Success, "A .vue file with no <template> is not a view.");

        return match.Groups["body"].Value;
    }

    /// <summary>
    /// The open tag of the element whose text contains <paramref name="index"/>, or null when the
    /// index is inside a tag rather than between two of them.
    /// </summary>
    private static string? ElementAround(string markup, int index)
    {
        var opened = markup.LastIndexOf('<', index);
        if (opened < 0) return null;

        var closed = TagEnd(markup, opened);

        // The index is inside the tag rather than in the text after it: an :aria-valuenow, or a
        // v-if, which is script and not something a reader is laid out.
        return closed < 0 || closed >= index ? null : markup[opened..(closed + 1)];
    }

    /// <summary>
    /// The '&gt;' that actually ends the tag opening at <paramref name="at"/>, ignoring the ones
    /// inside attribute values.
    ///
    /// <para>Not pedantry: <c>v-if="a.length &gt; 0"</c> is ordinary Vue, and reading the first
    /// '&gt;' as the end of the tag cuts the element in half. That reported this test's own template
    /// as a defect once — the visible failure — and the invisible one is the other direction, where a
    /// mis-bounded tag swallows the <c>dir="ltr"</c> of the element it belongs to and a genuinely
    /// undirected figure goes unnoticed.</para>
    /// </summary>
    private static int TagEnd(string markup, int at)
    {
        var quote = '\0';

        for (var i = at; i < markup.Length; i++)
        {
            var c = markup[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The braces of the block a Razor conditional opens after <paramref name="at"/>.</summary>
    private static (int Start, int End) BlockAfter(string source, int at)
    {
        var start = source.IndexOf('{', at);
        Assert.True(start > 0, "The conditional opens no block.");

        var depth = 0;

        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return (start, i);
        }

        throw new InvalidOperationException("The block opened at index " + start + " is never closed.");
    }

    private static int Layer(string css, string selector)
    {
        var value = Rule(css, selector).Value("z-index");

        Assert.True(value is not null, $"`{selector}` declares no z-index; the stacking order moved.");

        return int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AppCss() => Read("src/DriveUnion.Web/wwwroot/css/app.css");

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root().FullName, relativePath));

    private static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No DriveUnion.slnx above {AppContext.BaseDirectory}; this test reads the repository's own source.");
    }

    // ------------------------------------------------------------------ a very small CSS reader

    /// <summary>
    /// The same reader <c>PanelLayoutTests</c> uses, and deliberately a second copy of it: that one
    /// is private to a file whose subject is the panel's layout, and lifting it into a shared helper
    /// is a change to a file this work does not own.
    /// </summary>
    private sealed record CssRule(string Selector, string Body)
    {
        public string? Value(string property)
        {
            var match = Regex.Match(
                Body,
                $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*(?<value>[^;]+)",
                RegexOptions.Multiline,
                Timeout);

            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }
    }

    private static CssRule Rule(string css, string selector)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, Timeout);

        var rule = Regex
            .Matches(stripped, @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.None, Timeout)
            .Select(m => new CssRule(m.Groups["sel"].Value.Trim(), m.Groups["body"].Value))
            .FirstOrDefault(r => r.Selector == selector);

        Assert.True(rule is not null, $"No rule for `{selector}`; it was renamed or removed.");

        return rule!;
    }
}
