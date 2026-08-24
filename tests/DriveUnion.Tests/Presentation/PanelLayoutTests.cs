using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Links;
using DriveUnion.Tests.Plans;
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

    // ------------------------------------------------------------------ direction

    /// <summary>
    /// Every panel screen that draws a byte quantity, checked for the isolate that keeps it readable.
    ///
    /// This is the defect the owner is most likely to be looking at, and it is not subtle once you
    /// know the arithmetic. «5 GB» in an RTL box is a European number, a neutral space and a Latin
    /// run: the bidi algorithm treats the number as right-to-left when it resolves the space between
    /// them, so the space takes the paragraph's direction, the two halves become separate runs, and
    /// the browser lays them out as «GB 5». The same rule turns «0 B / 202 MB» into
    /// «B / 202 MB 0» — which is the exact shape this was first reported as.
    /// </summary>
    [Theory]
    [InlineData("/files")]
    [InlineData("/links")]
    [InlineData("/plans")]
    [InlineData("/operator/plans")]
    [InlineData("/design")]
    public async Task A_latin_readout_carries_its_own_direction(string path)
    {
        var markup = await MarkupAsync(path);

        var offenders = Leaves(markup)
            .Where(leaf => IsLatinReadout(leaf.Text))
            .Where(leaf => !leaf.Attributes.Contains(@"dir=""ltr""", StringComparison.Ordinal))
            .Select(leaf => leaf.Text.Trim())
            .ToList();

        offenders.Should().BeEmpty(
            "{0} draws these as Latin text in a right-to-left box with nothing isolating them, so "
            + "each is laid out with its unit before its number",
            path);
    }

    /// <summary>
    /// The reader above is really finding byte sizes.
    ///
    /// Without this, an escaping change or a renamed class would leave the theory scanning a page it
    /// recognises nothing on, and the guard would pass on nothing at all. The catalogue table draws
    /// three of them per tier — storage, per-file, traffic — and the seeded catalogue has tiers.
    /// </summary>
    [Fact]
    public async Task The_reader_finds_the_byte_sizes_it_is_looking_for()
    {
        var readouts = Leaves(await MarkupAsync("/operator/plans"))
            .Where(leaf => IsLatinReadout(leaf.Text))
            .ToList();

        readouts.Should().HaveCountGreaterThanOrEqualTo(
            3,
            "the plan catalogue draws a storage, a per-file and a traffic ceiling for every tier");
    }

    /// <summary>
    /// The two quota history tables are one table drawn twice, and they have to stay that shape.
    ///
    /// The workspace screen used to merge «از» and «به» into a single «تغییر» cell built from one
    /// string — «500 GB ← 1 TB». Nothing in a view can isolate half of a string, so that cell had
    /// no fix short of splitting it: it rendered «GB ← 1 TB 500». Two cells can each carry a
    /// direction, and the reading order the arrow stood in for is now the column order.
    /// </summary>
    [Fact]
    public void Both_quota_histories_give_the_old_value_and_the_new_one_their_own_column()
    {
        foreach (var view in new[]
        {
            "src/DriveUnion.Web/Views/Plans/OperatorTenant.cshtml",
            "src/DriveUnion.Web/Views/Tenants/Detail.cshtml",
        })
        {
            var source = Read(view);

            source.Should().Contain("UiText.Plans.ColumnFrom", $"{view} draws a quota history");
            source.Should().Contain("UiText.Plans.ColumnTo", $"{view} draws a quota history");
            source.Should().NotContain(
                "UiText.Tenants.FromTo",
                $"{view} would be building «500 GB ← 1 TB» as one unisolatable string again");
        }
    }

    // ------------------------------------------------------------------ what does not move

    /// <summary>
    /// A table declares exactly as many tracks as it draws columns.
    ///
    /// The tracks are written per screen in a <c>--cols</c> and the header cells are written in the
    /// markup beside them, so the two are one decision in two places. A track too few and the last
    /// column is drawn on top of the one before it; a track too many and every row ends in a gap the
    /// header does not have. Counting them is the only thing standing between the two copies.
    /// </summary>
    [Theory]
    [InlineData("/files")]
    [InlineData("/links")]
    [InlineData("/operator/plans")]
    [InlineData("/design")]
    public async Task A_table_declares_as_many_tracks_as_it_draws_columns(string path)
    {
        var tables = TablesIn(await MarkupAsync(path));

        tables.Should().NotBeEmpty($"{path} is listed here because it draws a .dtable");

        foreach (var (cols, headCells) in tables)
        {
            TrackCount(cols).Should().Be(
                headCells,
                "«{0}» on {1} declares {2} tracks for {3} header cells",
                cols,
                path,
                TrackCount(cols),
                headCells);
        }
    }

    /// <summary>
    /// The theme control is mounted by Vue, so its box has to exist before the bundle does.
    ///
    /// Until it lands the header holds an empty span, and the language form and «آپلود فایل» beside
    /// it are laid out where the button is not yet — then they move. The floor cannot be
    /// server-rendered inside the mount point, because mounting replaces its contents: a placeholder
    /// button would be painted and thrown away.
    /// </summary>
    [Fact]
    public void The_theme_control_reserves_the_box_it_will_appear_into()
    {
        var slot = Rule(AppCss(), @"[data-island=""theme-language""]");

        // A .btn--sm is 12px of text at line-height 1.4 between 6px paddings and a 1px border.
        slot.Value("min-block-size").Should().Be("31px");

        // …and one 13px glyph between 12px paddings and the same border.
        slot.Value("min-inline-size").Should().Be("39px");
    }

    /// <summary>
    /// A fragment link does not land under the sticky header.
    ///
    /// The style guide's chip row is six links into its own headings, and the header above them is
    /// <c>position: sticky</c>: without a scroll margin the browser puts the heading at y = 0 and
    /// the header covers it. accounts.css settled the number for the setup panel first — a .btn--lg
    /// between the header's 14px paddings, plus air — and this is the same header.
    /// </summary>
    [Fact]
    public void A_fragment_link_does_not_land_under_the_sticky_header()
    {
        Rule(AppCss(), ".section-title").Value("scroll-margin-block-start").Should().Be("80px");
        Rule(AccountsCss(), ".setup").Value("scroll-margin-block-start").Should().Be("80px");
    }

    /// <summary>
    /// The rows that hold a title on one side and controls on the other wrap rather than squeeze.
    ///
    /// The header already learned this the expensive way — 505px of content in a 375px box, with the
    /// whole action group laid out at x = -130. These four are the same shape: a card head carrying
    /// a title, a sentence and a button; a table foot carrying a file name and two forms; a public
    /// card's foot carrying a share address and a count; a page title beside a back link.
    /// </summary>
    [Theory]
    [InlineData(".card-head")]
    [InlineData(".dtable-foot")]
    [InlineData(".public-foot")]
    [InlineData(".page-head")]
    public void A_row_of_controls_wraps_before_it_leaves_the_card(string selector) =>
        Rule(AppCss(), selector).Value("flex-wrap").Should().Be("wrap");

    // ------------------------------------------------------------------ one product, one component

    /// <summary>
    /// Six screens drew «what the server said back» as a .card wrapping a .card-body wrapping a
    /// sized span — and none of them could carry a role, because the sentence had no element of its
    /// own to put one on. An operator on a screen reader was told nothing had happened at all.
    /// </summary>
    [Fact]
    public void A_notice_says_out_loud_that_something_happened()
    {
        var mute = new List<string>();

        foreach (var view in PanelViews())
        {
            foreach (Match tag in Regex.Matches(
                Read(view),
                @"<\w+[^>]*class=""notice[^""]*""(?<rest>[^>]*)>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
            {
                if (!tag.Groups["rest"].Value.Contains("role=", StringComparison.Ordinal)) mute.Add(view);
            }
        }

        mute.Should().BeEmpty("a notice with no role is a sentence only a sighted reader is told");
    }

    /// <summary>
    /// The box a control is drawn in is a class, and the panel has two of them for two jobs:
    /// <c>.field</c> for a control that shares its box with something else — a prefix, a copy
    /// button, a unit — and <c>.control</c> for one that does not.
    ///
    /// Five screens reached for .field as the wrapper around a label and an input instead, which
    /// drew a bordered monospace box around the label, stood the label beside the input rather than
    /// above it, and then drew the input's own border inside it — because each of those screens also
    /// wrote .field's declarations out again in a style attribute on the control. Ten copies, four
    /// of them subtly different heights.
    /// </summary>
    [Fact]
    public void The_box_a_control_is_drawn_in_is_a_class()
    {
        foreach (var view in PanelViews())
        {
            var source = Read(view);

            Regex.IsMatch(
                source,
                @"style=""[^""]*border(-radius)?\s*:",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
                .Should().BeFalse($"{view} draws a box in a style attribute; .field and .control are the two boxes");

            Regex.IsMatch(
                source,
                @"<div class=""field""[^>]*>\s*<label",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
                .Should().BeFalse($"{view} uses .field as a wrapper; .form-field is the wrapper");
        }
    }

    /// <summary>
    /// The colour utilities are the last rules in the stylesheet that name a colour.
    ///
    /// The same trap as the hamburger, from the other side. <c>.danger</c> is one class deep and so
    /// is every component it corrects — <c>.page-sub.danger</c> on a refusal, <c>.card-title.warn</c>
    /// on a warning heading — and at equal specificity the later rule wins. Written where they
    /// started, up beside .muted, .page-sub's own `color: var(--muted)` outranked them and the
    /// Telegram screens' refusals rendered grey.
    /// </summary>
    [Fact]
    public void A_colour_utility_is_written_after_the_components_it_corrects()
    {
        var coloured = Rules(AppCss())
            .Where(r => r.Declares("color"))
            .Select(r => r.Selector)
            .ToList();

        coloured.Should().EndWith([".warn", ".danger"]);
    }

    /// <summary>
    /// No screen names a colour in a style attribute.
    ///
    /// Every colour in this product is a token, and a token named in an attribute is one no
    /// stylesheet test can see and no theme audit can find. .muted, .warn and .danger are the three
    /// the panel actually uses. One exception is left and it is real: <c>Views/Links</c> writes
    /// <c>style="@row.StatusStyle"</c>, where the colour is decided by the view model — the source
    /// still names none.
    /// </summary>
    [Fact]
    public void No_screen_names_a_colour_in_a_style_attribute()
    {
        foreach (var view in PanelViews())
        {
            Regex.IsMatch(
                Read(view),
                @"style=""[^""]*(color|background)\s*:",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
                .Should().BeFalse($"{view} carries a colour an audit of the stylesheet cannot see");
        }
    }

    // ------------------------------------------------------------------ reading the source

    /// <summary>
    /// The panel's own views.
    ///
    /// <c>Views/Design</c> is out because the style guide paints tokens for a living: its swatches
    /// <i>are</i> <c>background: var(--bg)</c> in an attribute, computed per row. <c>Views/Accounts</c>
    /// and <c>Views/Files/Upload.cshtml</c> are out because they belong to work in flight beside this
    /// one, and <c>Areas/Identity</c> is not under this root at all — each of them is a folder to
    /// add here when its owner has swept it, the same way MigratedScreensTests grows.
    /// </summary>
    private static IEnumerable<string> PanelViews()
    {
        var root = RepositoryRoot();
        var views = new DirectoryInfo(Path.Combine(root.FullName, "src/DriveUnion.Web/Views"));

        Assert.True(views.Exists, "src/DriveUnion.Web/Views does not exist; this test reads the panel's own source.");

        foreach (var file in views.EnumerateFiles("*.cshtml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

            if (relative.Contains("/Views/Design/", StringComparison.Ordinal)) continue;
            if (relative.Contains("/Views/Accounts/", StringComparison.Ordinal)) continue;
            if (relative.EndsWith("/Views/Files/Upload.cshtml", StringComparison.Ordinal)) continue;

            yield return relative;
        }
    }

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

    /// <summary>
    /// A panel screen, rendered by the real pipeline for a caller who can reach all of them.
    ///
    /// Both claims on purpose: these paths are split between the customer's panel and the
    /// operator's, and what is under test here is the markup rather than the policy — which
    /// <c>PanelPolicyTests</c> and <c>OperatorOnlyRouteTests</c> already hold.
    /// </summary>
    private static async Task<string> MarkupAsync(string path)
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id, asOperator: true);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{path} answered {(int)response.StatusCode}; this test reads what it renders.");

        return await response.Content.ReadAsStringAsync();
    }

    // ------------------------------------------------------------------ a very small HTML reader

    /// <summary>An element with no element inside it: its attributes as written, and its text.</summary>
    private sealed record Leaf(string Attributes, string Text);

    private static IEnumerable<Leaf> Leaves(string markup) => Regex
        .Matches(
            markup,
            @"<(?<tag>div|span)(?<attrs>[^>]*)>(?<text>[^<>]*)</\k<tag>>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5))
        .Select(m => new Leaf(m.Groups["attrs"].Value, m.Groups["text"].Value));

    /// <summary>
    /// A byte quantity and nothing else: <c>5 GB</c>, <c>18.4 MB</c>, <c>0 B / 202 MB</c>.
    ///
    /// Anchored at both ends so a sentence that merely mentions a size does not match. A sentence
    /// cannot be fixed from a view anyway — the isolate would have to go around the interpolated
    /// half, where the sentence is assembled.
    /// </summary>
    private static bool IsLatinReadout(string text) => Regex.IsMatch(
        text,
        @"^\s*[\d.,]+\s+[A-Za-z]{1,3}(\s*/\s*[\d.,]+\s+[A-Za-z]{1,3})?\s*$",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>Each <c>.dtable</c> on a page, with the tracks it declares and the columns it draws.</summary>
    private static List<(string Cols, int HeadCells)> TablesIn(string markup)
    {
        var tables = new List<(string, int)>();

        foreach (Match table in Regex.Matches(
            markup,
            @"class=""dtable""\s+style=""(?<cols>--cols:[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)))
        {
            // A header cell holds text and never an element, which is what lets this stop at the
            // end of the header instead of walking into the first row.
            var head = Regex.Match(
                markup[table.Index..],
                @"<div class=""dtable-head"">(?<cells>(?:\s*<div[^>]*>[^<]*</div>)+)\s*</div>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            Assert.True(
                head.Success,
                $"A .dtable carrying «{table.Groups["cols"].Value}» has no header to compare it with.");

            tables.Add((
                table.Groups["cols"].Value,
                Regex.Matches(head.Groups["cells"].Value, "<div", RegexOptions.None, TimeSpan.FromSeconds(5)).Count));
        }

        return tables;
    }

    /// <summary>
    /// Top-level tracks in a <c>--cols</c>, so <c>minmax(var(--name-min), 2.4fr)</c> counts once and
    /// the space inside its parentheses is not mistaken for the space between two tracks.
    /// </summary>
    private static int TrackCount(string cols)
    {
        var value = cols[(cols.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var tracks = 0;
        var depth = 0;
        var inTrack = false;

        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;

            if (depth == 0 && char.IsWhiteSpace(c))
            {
                inTrack = false;
                continue;
            }

            if (inTrack) continue;

            inTrack = true;
            tracks++;
        }

        return tracks;
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
