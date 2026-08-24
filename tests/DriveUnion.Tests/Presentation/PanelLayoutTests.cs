using System.Text.RegularExpressions;
using DriveUnion.Tests.Links;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The panel's layout rules that a browser proved and that no browser is available to re-prove.
///
/// Every assertion here started as a measurement. They are written against the stylesheet's text and
/// the panel's rendered markup because that is the half of each defect a test can hold: the numbers
/// — 130px of header laid out past the edge of a 375px window, a name column resolved to 0px, a
/// sign-out button 32.7px outside the sidebar's padding — are in the comments, and are recorded so
/// that whoever changes one of these rules knows what it was for.
///
/// What this file cannot see is stated where it applies: no layout is performed here, so a rule that
/// is present but wrong is invisible to it. It catches the rule going away, which is how all three
/// of the defects behind it were introduced.
/// </summary>
public class PanelLayoutTests
{
    /// <summary>
    /// The content width the narrowest supported window leaves a full-bleed card.
    ///
    /// 375px viewport, minus <c>.app-content</c>'s 16px padding either side under the 900px media
    /// query, minus the card's 1px border either side. Every fixed-track sum below is compared with
    /// this number, because it is the one that decided the defect.
    /// </summary>
    private const int NarrowestCardContent = 375 - (16 * 2) - (1 * 2);

    [Fact]
    public void The_menu_button_is_hidden_by_a_rule_the_button_style_cannot_outrank()
    {
        // `.nav-toggle { display: none }` and `.btn { display: inline-flex }` are both one class
        // deep, so the later of the two won and a 37×31px hamburger sat in the header at 1280 and
        // 1440 in both directions. Naming .btn in the same compound is what settles it whichever
        // block moves.
        var offenders = Rules(AppCss())
            .Where(r => r.Selector.Contains("nav-toggle", StringComparison.Ordinal))
            .Where(r => r.Declares("display"))
            .Where(r => !r.Selector.Contains(".btn", StringComparison.Ordinal))
            .Select(r => r.Selector)
            .ToList();

        offenders.Should().BeEmpty(
            "a display rule for .nav-toggle that does not also name .btn is outranked by "
            + ".btn { display: inline-flex } and puts the hamburger on every desktop");
    }

    [Fact]
    public async Task The_menu_button_wears_both_classes_the_rule_matches_on()
    {
        // The other half of the rule above: the selector is only worth its specificity if the
        // markup still gives the control both classes.
        var markup = await ShellAsync();

        markup.Should().MatchRegex(
            @"<button[^>]*class=""[^""]*\bbtn\b[^""]*\bnav-toggle\b[^""]*""",
            "app.css hides the toggle with .btn.nav-toggle, which matches nothing if the markup "
            + "drops either class");
    }

    [Fact]
    public void The_header_wraps_rather_than_laying_its_actions_off_the_edge()
    {
        // The search box will not shrink past its own min-content — an <input> contributes its
        // size=20 width whatever min-width says — so at 375px the header measured 505px of content
        // and the theme, language and upload controls were laid out at x = -130 and clipped away by
        // the shell. Wrapping costs a row of header height on a phone and nothing on a desktop.
        Rule(AppCss(), ".app-header").Value("flex-wrap").Should().Be("wrap");
    }

    [Fact]
    public void The_signed_in_row_gives_before_the_sign_out_control_does()
    {
        var css = AppCss();

        // Without min-width: 0 the name block refuses to shrink below its min-content, and an email
        // address has almost no break opportunity: «operator@driveunion.test» pushed the sign-out
        // button 32.7px past the sidebar's 12px padding and 20.7px past its border, over the
        // content column, at 1280 and 1440 in RTL.
        Rule(css, ".sidebar-identity-text").Value("min-width").Should().Be("0");

        // …and shrinking has to end in an ellipsis rather than in a clipped address.
        Rule(css, ".sidebar-identity-name,\n.sidebar-identity-role")
            .Value("text-overflow").Should().Be("ellipsis");

        // The one thing in the row that must never be what gives.
        Rule(css, ".sidebar-identity-out").Value("flex").Should().Be("0 0 auto");
    }

    [Fact]
    public async Task The_signed_in_row_renders_the_classes_those_rules_are_written_for()
    {
        var markup = await ShellAsync();

        markup.Should().Contain("sidebar-identity-text");
        markup.Should().Contain("sidebar-identity-name");
        markup.Should().Contain("sidebar-identity-role");
        markup.Should().Contain("sidebar-identity-out");
    }

    [Fact]
    public void A_name_column_has_a_floor_and_the_table_can_scroll_to_the_rest()
    {
        var dtable = Rule(AppCss(), ".dtable");

        // `minmax(0, Nfr)` answers "there is no room" by resolving to zero: at 375px the files
        // table's name column was laid out 0px wide and every row showed four values and a blank.
        dtable.Value("--name-min").Should().Be("160px");

        // With a floor the grid can be wider than its card, so it carries its own scroll region.
        dtable.Value("overflow-x").Should().Be("auto");
    }

    [Theory]
    [InlineData("/files")]
    [InlineData("/links")]
    public async Task Every_panel_table_declares_a_name_track_that_cannot_resolve_to_nothing(string path)
    {
        var cols = await ColumnsAsync(path);

        // The reason, restated as arithmetic so a future edit to the tracks re-reads it: the fixed
        // columns alone are wider than the card at 375px, so the flexible one is what a browser
        // takes the shortfall out of.
        var fixedTracks = Regex
            .Matches(cols, @"(?<!\()\b(\d+(?:\.\d+)?)px", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Sum();

        fixedTracks.Should().BeGreaterThan(
            NarrowestCardContent,
            "if the fixed tracks ever fit the narrowest card this test is no longer about anything");

        cols.Should().Contain(
            "minmax(var(--name-min)",
            "a zero minimum is how the name column came to be drawn 0px wide");
    }

    [Fact]
    public void A_table_row_is_a_box_because_on_two_screens_it_is_the_link()
    {
        var row = Rule(AppCss(), ".dtable-row");

        // A `display: contents` element generates no box and Chrome will not focus one. Measured on
        // /files: `.focus()` on a row left document.activeElement where it was, `tabindex="0"`
        // changed nothing, and all nine other controls on the page took focus — so the only way to
        // open a file's detail panel, or to reach a link's file from /links, was a mouse.
        row.Value("display").Should().Be("grid");
        row.Value("grid-column").Should().Be("1 / -1");

        // subgrid is what keeps the cells on the header's own tracks now that the row is a box.
        row.Value("grid-template-columns").Should().Be("subgrid");
    }

    [Fact]
    public void A_table_row_does_not_take_the_base_link_colour_with_it()
    {
        var css = AppCss();

        // The row is an anchor, so `a { color: var(--accent) }` and `a:hover { text-decoration:
        // underline }` reach it: measured, every file name in both tables rendered #0F9D77 rather
        // than --text. The comp paints one column in the accent — the slug — and that is what the
        // colour is for.
        Rule(css, ".dtable-row").Value("color").Should().Be("var(--text)");
        Rule(css, ".dtable-row:hover").Value("text-decoration").Should().Be("none");

        // …and the one column that is meant to be accent still says so for itself.
        Rule(css, ".cell-slug").Value("color").Should().Be("var(--accent-ink)");
    }

    [Fact]
    public void The_empty_state_is_as_wide_as_what_can_be_seen_and_not_as_wide_as_the_tracks()
    {
        var css = AppCss();

        // A cell spanning every track is as wide as the grid: in an empty files table at 375px that
        // was 688px against 288px of visible card, and the centred message put its action button at
        // x = -58, off the edge of a table with nothing in it to scroll to.
        Rule(css, ".empty--incell").Value("width").Should().Be("100cqi");

        // …which only means the scrollport because the table says so.
        Rule(css, ".dtable").Value("container-type").Should().Be("inline-size");
    }

    [Fact]
    public void Nothing_above_a_sticky_box_becomes_a_scroll_container()
    {
        // `overflow: hidden|auto|scroll` on an ancestor makes `position: sticky` resolve against
        // that box instead of the viewport. The shell already uses `clip` for exactly this reason;
        // the table's own `overflow-x: auto` is safe only because nothing inside a .dtable sticks.
        Rule(AppCss(), ".app-shell").Value("overflow-x").Should().Be("clip");

        var sticky = new[] { ".app-header", ".app-sidebar", ".split-aside" };
        var ancestors = new[] { ".app-shell", ".app-main", ".app-content", ".split" };

        foreach (var selector in ancestors)
        {
            foreach (var rule in Rules(AppCss()).Where(r => r.Selector == selector))
            {
                foreach (var property in new[] { "overflow", "overflow-x", "overflow-y" })
                {
                    var value = rule.Value(property);
                    if (value is null) continue;

                    value.Should().NotBe("hidden", $"{selector} is above {string.Join(", ", sticky)}");
                    value.Should().NotBe("auto", $"{selector} is above {string.Join(", ", sticky)}");
                    value.Should().NotBe("scroll", $"{selector} is above {string.Join(", ", sticky)}");
                }
            }
        }
    }

    [Fact]
    public void No_rule_in_the_panels_stylesheets_names_a_physical_side()
    {
        // The panel is RTL Persian and LTR English out of one stylesheet. A single `padding-left`
        // is a second stylesheet. The two places that genuinely cannot use logical properties —
        // translateX on the two drawers — already carry [dir="rtl"] overrides.
        var physical = new Regex(
            @"(^|[;{\s])(margin|padding|border)-(left|right)\b|(^|[;{\s])(left|right)\s*:",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        foreach (var (name, css) in new[] { ("app.css", AppCss()), ("accounts.css", AccountsCss()) })
        {
            physical.IsMatch(StripComments(css)).Should().BeFalse($"{name} must not name a side");
        }
    }

    [Fact]
    public void The_setup_panels_rows_wrap_before_they_squeeze_the_value_out()
    {
        var css = AccountsCss();

        // Measured in English at 375px: the label held its 96px, the «From the server
        // configuration» badge held its 164.5px, and the Client ID, the stored-secret state and the
        // redirect URI were each laid out 2.5px wide. Persian never showed it — its badge is short
        // enough to leave room, which is why this had to be measured in both.
        Rule(css, ".setup-state-row").Value("flex-wrap").Should().Be("wrap");
        Rule(css, ".setup-state-value").Value("flex").Should().Be("1 1 140px");
    }

    [Fact]
    public void Googles_own_unbreakable_strings_break_rather_than_widen_the_card()
    {
        var css = AccountsCss();

        // A redirect URI is one token with no break opportunity in it: the effective one measured
        // 317px inside a 289px box at 375px and hung over the card's edge.
        Rule(css, ".setup-steps").Value("overflow-wrap").Should().Be("anywhere");
        Rule(css, ".setup-note").Value("overflow-wrap").Should().Be("anywhere");
    }

    // ------------------------------------------------------------------ reading the source

    private static string AppCss() => Read("src/DriveUnion.Web/wwwroot/css/app.css");

    private static string AccountsCss() => Read("src/DriveUnion.Web/wwwroot/css/accounts.css");

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot().FullName, relativePath));

    private static DirectoryInfo RepositoryRoot()
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

    /// <summary>The panel shell, rendered by the real pipeline for a signed-in customer.</summary>
    private static async Task<string> ShellAsync()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        return await harness.NewClient(tenant.Id).GetStringAsync("/files");
    }

    /// <summary>The <c>--cols</c> a screen writes on its table.</summary>
    private static async Task<string> ColumnsAsync(string path)
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        var markup = await harness.NewClient(tenant.Id).GetStringAsync(path);

        var match = Regex.Match(
            markup,
            @"class=""dtable""\s+style=""(?<cols>--cols:[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"{path} rendered no .dtable carrying a --cols.");

        return match.Groups["cols"].Value;
    }

    // ------------------------------------------------------------------ a very small CSS reader

    /// <summary>One declaration block, with the selector exactly as it is written.</summary>
    private sealed record CssRule(string Selector, string Body)
    {
        public bool Declares(string property) => Value(property) is not null;

        /// <summary>The declared value, trimmed, or null when the block does not set it.</summary>
        public string? Value(string property)
        {
            var match = Regex.Match(
                Body,
                $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*(?<value>[^;]+)",
                RegexOptions.Multiline,
                TimeSpan.FromSeconds(5));

            return match.Success ? match.Groups["value"].Value.Trim() : null;
        }
    }

    /// <summary>
    /// Innermost declaration blocks only. The pattern cannot match across a brace, so a rule inside
    /// an <c>@media</c> is returned with its own selector and the at-rule's prelude is skipped —
    /// which is what every assertion here wants, because none of them is about a breakpoint.
    /// </summary>
    private static List<CssRule> Rules(string css) => Regex
        .Matches(StripComments(css), @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(5))
        .Select(m => new CssRule(m.Groups["sel"].Value.Trim(), m.Groups["body"].Value))
        .ToList();

    /// <summary>The first block for a selector. Fails loudly rather than asserting against nothing.</summary>
    private static CssRule Rule(string css, string selector)
    {
        var rule = Rules(css).FirstOrDefault(r => r.Selector == selector);

        Assert.True(rule is not null, $"No rule for `{selector}`; it was renamed or removed.");

        return rule!;
    }

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));
}
