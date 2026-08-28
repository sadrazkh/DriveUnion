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

    /// <summary>
    /// A row's controls stay against the visible edge when the tracks are wider than the card.
    ///
    /// .dtable scrolls sideways rather than spilling, and that is right — but it chooses what leaves
    /// the screen by track order alone, and every one of these tables puts its buttons in the last
    /// track. Measured on /trash at a 560px viewport: 576px of tracks in a 526px scrollport, so the
    /// «Restore» button was cut 34px in and read «Res», directly above an «Empty the trash» button
    /// standing whole 17px inside the same card. Nothing was unreachable — a swipe brought it back —
    /// and that is the trap: the affordance is a hairline scrollbar under a row and the thing behind
    /// it is the only way to act on that row.
    ///
    /// Sticky on a grid item is not obviously legal, because a grid item's containing block is its
    /// own grid area and that would make the rule a no-op. Measured in Chrome rather than assumed:
    /// the cell's right edge moved from 593px to 543px — the scrollport's edge — and stayed there at
    /// every scroll offset, in both directions.
    /// </summary>
    [Fact]
    public void A_pinned_column_keeps_the_edge_and_stays_opaque_in_every_row_state()
    {
        var css = AppCss();
        var pin = Rule(css, ".cell-pin");

        pin.Value("position").Should().Be("sticky");

        // The logical form, and it has to be: the same panel lays out RTL, where the inline end is
        // the left edge. Measured with the document flipped — the button pinned at 32px from the
        // card's left edge and held it at both ends of the scroll.
        pin.Value("inset-inline-end").Should().Be("0");

        // Opaque, or the columns it is holding back are read through it — and opaque in each state
        // the unpinned cells have, or the pinned column lights up differently from the row it is in.
        Rule(css, ".dtable-head > .cell-pin").Value("background").Should().Be("var(--surface2)");
        Rule(css, ".dtable-row > .cell-pin").Value("background").Should().Be("var(--surface)");
        Rule(css, ".dtable-row:hover > .cell-pin").Value("background").Should().Be("var(--surface2)");

        // Found by predicate rather than by name: the selected pair is written one selector per
        // line like the unpinned rule above it, and Rule() compares the whole prelude.
        Rules(css)
            .Single(r => r.Selector.Contains(".is-selected", StringComparison.Ordinal)
                && r.Selector.Contains(".cell-pin", StringComparison.Ordinal))
            .Value("background").Should().Be("var(--soft)");
    }

    /// <summary>
    /// A screen that pins a column pins it in the header too, and pins nothing but the last one.
    ///
    /// Two ways to hold the rule and get nothing: pin the body cells and leave the header cell to
    /// scroll, and the column keeps its buttons but loses the heading that named them — worse, the
    /// heading of whatever column slides under it takes its place. Pin something that is not the
    /// last track and it is drawn over the columns after it rather than the ones before.
    /// </summary>
    [Fact]
    public void A_screen_that_pins_a_column_pins_the_same_one_in_its_header()
    {
        var pinned = PanelViews()
            .Select(view => (view, source: Read(view)))
            .Where(v => v.source.Contains("cell-pin", StringComparison.Ordinal))
            .ToList();

        pinned.Should().NotBeEmpty("/trash and the tier table both pin their action column");

        foreach (var (view, source) in pinned)
        {
            // Position rather than a parse of the header block: a .dtable draws its head before its
            // rows, so the first pin in the file is the header's if there is one at all. The header
            // cells carry Razor calls and comments between them, and a regex that walks them is a
            // second thing to keep true.
            var firstPin = source.IndexOf("cell-pin", StringComparison.Ordinal);
            var firstRow = source.IndexOf(@"class=""dtable-row", StringComparison.Ordinal);

            firstRow.Should().BeGreaterThan(-1, $"{view} pins a column in a table that draws no rows");

            firstPin.Should().BeLessThan(
                firstRow,
                $"{view} pins body cells and lets its header scroll out from over them, so the "
                + "column keeps its buttons and picks up whichever heading drifts above it");
        }
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

    /// <summary>
    /// The watch stage clips what overflows it, so its height may not be fixed by a ratio.
    ///
    /// <para><b>This is the bug that made a locked film unplayable.</b> The stage was
    /// <c>aspect-ratio: 16 / 9</c> with <c>overflow: hidden</c>, and the unlock card inside it is
    /// 346px tall — taller than the stage is at every phone width, because 16/9 of 375px is 211px.
    /// A flex box centring an item too big for it overflows at <i>both</i> ends, so 67px was cut off
    /// the top and 67px off the bottom, and the bottom 67px is where the Unlock button is. The film
    /// did not fail to play; there was no way to ask for it.</para>
    ///
    /// <para>A ratio is still what the box wants — a media element with no source has no height, and
    /// without one the page jumps by the height of a film the moment play is pressed. It has to be a
    /// floor rather than a height, which is what the <c>::before</c> spacer sharing a grid cell with
    /// the content does: the row is the taller of the two.</para>
    ///
    /// <para>Asserted against the stage and not against the card because the card's height is the
    /// sum of a dozen things and every one of them is allowed to change. What may not change is the
    /// box being unable to grow to hold it.</para>
    /// </summary>
    [Fact]
    public void The_watch_stage_does_not_fix_its_height_while_clipping_its_overflow()
    {
        foreach (var stage in Rules(AppCss()).Where(r => r.Selector is ".watch-stage"))
        {
            if (stage.Value("overflow") is not "hidden" and not "clip") continue;

            stage.Declares("aspect-ratio").Should().BeFalse(
                "the unlock card is taller than 16/9 at every phone width, and a clipped box that "
                + "cannot grow eats the Unlock button");

            stage.Declares("block-size").Should().BeFalse("same reason: a fixed height cannot grow");
            stage.Declares("max-block-size").Should().BeFalse("a ceiling clips as surely as a height");
        }
    }

    [Fact]
    public void Nothing_above_a_sticky_box_becomes_a_scroll_container()
    {
        // `overflow: hidden|auto|scroll` on an ancestor makes `position: sticky` resolve against
        // that box instead of the viewport. The shell already uses `clip` for exactly this reason.
        //
        // The table's own `overflow-x: auto` is a scroll container, and .cell-pin sticks against it
        // on purpose — that is the whole of how a row's action column stays on screen. What this
        // test is about is the three boxes that must resolve against the viewport instead, and none
        // of them is inside a .dtable.
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

    // ------------------------------------------------------------------ the phone

    /// <summary>
    /// The shell gives back the strips of the screen the phone keeps for itself.
    ///
    /// <para>M1 declared <c>apple-mobile-web-app-status-bar-style: black-translucent</c> and both
    /// layouts carry <c>viewport-fit=cover</c>, which is what makes an installed panel fill the
    /// screen — and what lays it out <i>under</i> the clock and <i>under</i> the home indicator.
    /// Without these declarations the header renders behind the status bar and the sidebar's
    /// sign-out control behind the bar the reader swipes to leave the app.</para>
    ///
    /// <para>The tokens are asserted verbatim rather than by substring because the 0px fallbacks are
    /// load bearing: <c>env()</c> with no fallback resolves to nothing on a browser that does not
    /// know the keyword, and <c>calc(14px + )</c> is an invalid declaration rather than 14px — which
    /// would take the padding off the header on the desktop as well.</para>
    /// </summary>
    [Fact]
    public void The_shell_gives_the_phone_back_the_strips_it_keeps()
    {
        var css = AppCss();

        var tokens = Rules(css).Single(r => r.Selector == ":root" && r.Declares("--safe-inline"));

        tokens.Value("--safe-block-start").Should().Be("env(safe-area-inset-top, 0px)");
        tokens.Value("--safe-block-end").Should().Be("env(safe-area-inset-bottom, 0px)");

        // The larger of the two physical insets, given to both inline edges — see below for why.
        tokens.Value("--safe-inline").Should()
            .Be("max(env(safe-area-inset-left, 0px), env(safe-area-inset-right, 0px))");

        // Every box in the shell that touches an edge of the screen.
        Rule(css, ".app-header").Value("padding-block-start").Should().Contain("--safe-block-start");
        Rule(css, ".app-sidebar").Value("padding-block-start").Should().Contain("--safe-block-start");
        Rule(css, ".app-sidebar").Value("padding-block-end").Should().Contain("--safe-block-end");
        Rule(css, ".app-content").Value("padding-block-end").Should().Contain("--safe-block-end");

        // The dock sits in the corner the home indicator is in, so a press that misses «توقف» by a
        // few pixels is a swipe that leaves the app — which pauses the transfer the button was for.
        Rule(css, ".upload-dock").Value("inset-block-end").Should().Contain("--safe-block-end");

        // The cue that says a press was heard is a 2px line at the very top of the viewport.
        Rule(css, ".app-content[aria-busy='true']::before")
            .Value("inset-block-start").Should().Be("var(--safe-block-start)");
    }

    /// <summary>
    /// The one rule in these stylesheets that names a physical side names both of them.
    ///
    /// <para><c>env()</c> has no logical form, and this panel is RTL Persian and LTR English out of
    /// one file — so an inset applied to <c>left</c> alone is correct in one language and pads the
    /// wrong edge in the other. The answer is the larger of the two on both edges, which costs a
    /// strip of padding on the side away from the notch in landscape and nothing at all in
    /// portrait, where both are zero.</para>
    ///
    /// <para>Written as a per-declaration check rather than a count, because two declarations each
    /// naming one side would balance and still be wrong in both directions.</para>
    /// </summary>
    [Fact]
    public void An_inset_that_names_one_inline_edge_names_the_other()
    {
        foreach (var (name, css) in new[] { ("app.css", AppCss()), ("accounts.css", AccountsCss()) })
        {
            var offenders = Regex
                .Matches(
                    StripComments(css),
                    @"[^;{}]*safe-area-inset-(?:left|right)[^;{}]*",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Select(m => m.Value.Trim())
                .Where(declaration =>
                    !declaration.Contains("safe-area-inset-left", StringComparison.Ordinal)
                    || !declaration.Contains("safe-area-inset-right", StringComparison.Ordinal))
                .ToList();

            offenders.Should().BeEmpty(
                "{0} pads one physical edge, which is the inline start in one of this panel's two "
                + "languages and the inline end in the other",
                name);
        }
    }

    /// <summary>
    /// Below the phone breakpoint a table is a stack of records rather than a squeezed grid.
    ///
    /// <para>Every <c>.dtable</c> writes its own <c>--cols</c>; the narrowest is three columns and
    /// the operator's plan catalogue is nine, which is 906px of track against the 358px a 390px
    /// phone leaves a full-bleed card. What carried that until now was <c>overflow-x: auto</c> —
    /// nine columns read three at a time through a hairline scrollbar, which is also the whole
    /// reason <c>.cell-pin</c> had to be invented.</para>
    /// </summary>
    [Fact]
    public void A_table_below_the_phone_breakpoint_is_a_stack_of_records()
    {
        var css = AppCss();

        Newlines(css).Should().Contain(
            "@media (max-width: 640px)",
            "the panel's smallest breakpoint was 900px and the 760px one only touches the public card");

        // The grid and its sideways scroll both go.
        var phoneTable = Rules(css).Last(r => r.Selector == ".dtable");
        phoneTable.Value("display").Should().Be("block");
        phoneTable.Value("overflow-x").Should().Be("visible");

        // The header band goes with them, because there are no columns left for it to name.
        Rules(css).Last(r => r.Selector == ".dtable-head").Value("display").Should().Be("none");

        // …so each cell carries its own column's name. attr() and not a second copy of the word: the
        // value on the cell is the same UiText entry the header renders.
        Rule(css, ".dtable-row > [data-label]::before").Value("content").Should().Be("attr(data-label)");

        // A record is a flex column so that one declaration can lift the heading out of whichever
        // track the view wrote it in — /operator/plans leads with a tier's code and the member list
        // with an address, and neither is what somebody is looking down the list for.
        // Found by predicate: the pair is written one selector per line inside a media query, so the
        // prelude carries that block's indentation and Rule() compares the whole of it.
        var record = Rules(css).Single(r =>
            r.Selector.Contains(".dtable-row", StringComparison.Ordinal)
            && r.Selector.Contains(".skeleton-row", StringComparison.Ordinal));
        record.Value("display").Should().Be("flex");
        record.Value("flex-direction").Should().Be("column");

        // …and the cross axis is the inline one now, so the row's own `align-items: center` stops
        // meaning «content in the middle of the band» and starts meaning «every line as wide as its
        // own text». Measured at 390px: the «۱۸.۴ MB» cell came out 64px wide in a 455px record and
        // broke after the number, so every size in the table was set over two lines.
        record.Value("align-items").Should().Be("stretch");

        Rule(css, ".dtable-row > :not([data-label], .cell-pin)").Value("order").Should().Be("-1");

        // Half the cells in the panel carry dir="ltr", because «18.4 MB» in an RTL box is laid out
        // «MB 18.4». That attribute is on the cell and the cell is the flex container, so in the
        // Persian panel those cells put the label at the opposite edge from the ones beside them:
        // measured at 390px, «حجم» and «لینک» at x = 75 with «تاریخ تغییر» at x = 340, in one record.
        var isolated = Rule(css, @"[dir=""rtl""] .dtable-row > [dir=""ltr""][data-label]");
        isolated.Value("flex-direction").Should().Be("row-reverse");

        // Reversing the order moves the label's 38% box and leaves the word at the far side of it.
        isolated.Value("text-align").Should().Be("end");
    }

    /// <summary>
    /// 44px on anything a finger lands on — Apple's figure in the Human Interface Guidelines and
    /// WCAG 2.2's AAA target size.
    ///
    /// <para>Every control in this panel was drawn for a pointer: a <c>.btn--sm</c> is 30.8px tall,
    /// the sidebar's sign-out button 32.4px, a <c>.chip</c> 25px, the row checkbox 13px. On a
    /// desktop that is a dense panel; under a thumb it is a row of controls that are missed and
    /// pressed again.</para>
    /// </summary>
    [Fact]
    public void Everything_a_finger_lands_on_is_44px_below_the_phone_breakpoint()
    {
        var css = AppCss();

        var sized = Rules(css).Single(r =>
            r.Value("min-block-size") == "44px"
            && r.Selector.Contains(".search", StringComparison.Ordinal));

        foreach (var control in new[]
        {
            ".btn", ".nav-item", ".chip", ".choice", ".seg-option", ".control", ".field", ".search",
        })
        {
            sized.Selector.Should().Contain(
                control,
                "{0} is something a reader presses and it is under 44px tall as the comp draws it",
                control);
        }

        // The row's checkbox is the one control with no text beside it, so the cell it sits in is
        // the <label> — and the cell is the corner of the record, which is where the 44px goes.
        var tick = Rule(css, ".cell-tick");
        tick.Value("min-block-size").Should().Be("44px");
        tick.Value("min-inline-size").Should().Be("44px");
    }

    /// <summary>
    /// The files table's checkbox cell is the label, which is what makes it pressable.
    ///
    /// A 13px <c>input</c> centred in a 44px cell is still a 13px target: there is no text beside it
    /// for a label to have been wrapped around, so without this the only thing to aim at is the
    /// control. The cell is a <c>label</c> containing its own input, so the whole corner passes the
    /// press on.
    /// </summary>
    [Fact]
    public async Task The_row_checkbox_is_wrapped_in_the_cell_that_gives_it_a_target()
    {
        var markup = await ShellAsync();

        markup.Should().MatchRegex(
            @"<label class=""cell-tick"">\s*<input type=""checkbox""",
            "app.css gives .cell-tick 44px and the input 22px; only a label turns the difference "
            + "into somewhere a thumb can land");
    }

    /// <summary>
    /// The upload dock sizes its own controls, because the shell is not allowed to.
    ///
    /// Its styles are <c>scoped</c>, so every selector in them carries a <c>[data-v-…]</c> the
    /// stylesheet cannot outrank — and _HeadAssets loads the island CSS after app.css on purpose.
    /// «توقف» and «لغو» are 3px of padding round 11px of text, which is 21px tall, in the corner of
    /// the screen the home indicator is in.
    /// </summary>
    [Fact]
    public void The_upload_dock_sizes_its_own_controls_because_the_stylesheet_cannot()
    {
        var dock = Read("src/DriveUnion.Web/Scripts/islands/UploadDock.vue");

        dock.Should().Contain(
            "@media (max-width: 640px)",
            "the breakpoint is app.css's and the number is repeated here because a media query's "
            + "condition cannot be a custom property");

        dock.Should().Contain("min-block-size: 44px");
    }

    /// <summary>
    /// Every cell that carries a label carries one of its own table's column names.
    ///
    /// The label and the heading above it are one decision written twice — the same
    /// <c>UiText.…Column…</c> entry on the head cell and on each body cell — and below 640px the
    /// heading is not rendered at all, so a label that has drifted is a column named wrongly on
    /// every phone and correctly on every desktop.
    /// </summary>
    [Theory]
    [InlineData("/files")]
    [InlineData("/links")]
    [InlineData("/operator/plans")]
    [InlineData("/design")]
    public async Task Every_labelled_cell_names_a_column_its_own_table_draws(string path)
    {
        var tables = LabelledTablesIn(await MarkupAsync(path));

        tables.Should().NotBeEmpty($"{path} is listed here because it draws a .dtable");

        foreach (var table in tables)
        {
            var headings = table.Head.Select(cell => cell.Text).ToList();

            foreach (var label in table.Labels)
            {
                headings.Should().Contain(
                    label,
                    "«{0}» on {1} is not a column that table draws — its headings are «{2}»",
                    label,
                    path,
                    string.Join("», «", headings));
            }
        }
    }

    /// <summary>
    /// A table names every column but one on its cells, and the one it does not name is the record's
    /// heading.
    ///
    /// <para>This is the half of the rule above that catches a column being <i>added</i>. The header
    /// band is <c>display: none</c> on a phone, so a cell with no <c>data-label</c> is a value with
    /// nothing saying what it is — and in a table of eight figures that is seven anonymous numbers
    /// under a name.</para>
    ///
    /// <para>Two kinds of cell are exempt and both are exempt in the markup rather than here: a head
    /// cell with no text at all names nothing to begin with (the files table's checkbox column), and
    /// a <c>.cell-pin</c> is a column of buttons that name themselves. What is left is columns with
    /// names, and exactly one of those — the file's, the workspace's, the tier's — is the line the
    /// record is identified by.</para>
    /// </summary>
    [Theory]
    [InlineData("/files")]
    [InlineData("/links")]
    [InlineData("/operator/plans")]
    [InlineData("/design")]
    public async Task A_table_names_every_column_but_the_one_that_is_the_records_heading(string path)
    {
        // A table with no rows has no cells to have labelled — /design draws two of those on
        // purpose, for the loading state and the empty state.
        var tables = LabelledTablesIn(await MarkupAsync(path)).Where(t => t.HasRows).ToList();

        tables.Should().NotBeEmpty($"{path} is listed here because it draws a .dtable with rows in it");

        foreach (var table in tables)
        {
            var named = table.Head
                .Where(cell => cell.Text.Length > 0)
                .Where(cell => !cell.Attributes.Contains("cell-pin", StringComparison.Ordinal))
                .Select(cell => cell.Text)
                .ToList();

            var unlabelled = named.Except(table.Labels).ToList();

            unlabelled.Should().ContainSingle(
                "on {0} the columns «{1}» are named in a header no phone renders, and «{2}» of them "
                + "reach the cells — exactly one column is meant to be the record's heading",
                path,
                string.Join("», «", named),
                table.Labels.Count);
        }
    }

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
            // A header cell may hold text or an element, but never another <div> — which is what
            // lets this stop at the end of the header instead of walking into the first row.
            //
            // It used to say «text and never an element», and that stopped being true the moment
            // the files table's checkbox column grew a slot for a select-all box. The rule that was
            // actually load-bearing is the narrower one: rows are divs, cells are divs, and nothing
            // inside a cell is.
            var head = Regex.Match(
                markup[table.Index..],
                """<div class="dtable-head">(?<cells>(?:\s*<div[^>]*>(?:[^<]|<(?!/?div))*</div>)+)\s*</div>""",
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

    /// <summary>One <c>.dtable</c>: the columns it names, and the names its cells carry.</summary>
    private sealed record PanelTable(
        IReadOnlyList<HeadCell> Head,
        IReadOnlyList<string> Labels,
        bool HasRows);

    /// <summary>A header cell as written, and the heading it prints.</summary>
    private sealed record HeadCell(string Attributes, string Text);

    /// <summary>
    /// Each table on a page, cut at the next one.
    ///
    /// Tables do not nest, so the span between two <c>--cols</c> is one table and everything in it.
    /// The last one runs to the end of the document, which costs nothing: no <c>data-label</c> is
    /// written anywhere but on a row's cell.
    /// </summary>
    private static List<PanelTable> LabelledTablesIn(string markup)
    {
        var starts = Regex
            .Matches(
                markup,
                @"class=""dtable""\s+style=""--cols:[^""]+""",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(m => m.Index)
            .ToList();

        var tables = new List<PanelTable>();

        for (var i = 0; i < starts.Count; i++)
        {
            var extent = markup[starts[i]..(i + 1 < starts.Count ? starts[i + 1] : markup.Length)];

            // The same header pattern TablesIn uses, and for the same reason: a cell may hold text or
            // an element but never another <div>, which is what stops this walking into the rows.
            var head = Regex.Match(
                extent,
                """<div class="dtable-head">(?<cells>(?:\s*<div[^>]*>(?:[^<]|<(?!/?div))*</div>)+)\s*</div>""",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));

            Assert.True(head.Success, "A .dtable on this page has no header to compare its labels with.");

            var cells = Regex
                .Matches(
                    head.Groups["cells"].Value,
                    @"<div(?<attrs>[^>]*)>(?<inner>(?:[^<]|<(?!/?div))*)</div>",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Select(m => new HeadCell(m.Groups["attrs"].Value, Heading(m.Groups["inner"].Value)))
                .ToList();

            var labels = Regex
                .Matches(extent, @"data-label=""(?<value>[^""]*)""", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(m => WebUtility.HtmlDecode(m.Groups["value"].Value).Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            tables.Add(new PanelTable(
                cells,
                labels,
                extent.Contains(@"class=""dtable-row", StringComparison.Ordinal)));
        }

        return tables;
    }

    /// <summary>
    /// What a header cell says, with its markup taken off and its entities put back.
    ///
    /// Razor encodes every non-ASCII character it writes, in text and in an attribute alike, so a
    /// Persian heading and the <c>data-label</c> that repeats it both arrive as runs of
    /// <c>&amp;#x…;</c>. Decoding is what makes the two comparable — and what makes a failure
    /// message readable.
    /// </summary>
    private static string Heading(string inner) => WebUtility
        .HtmlDecode(Regex.Replace(inner, "<[^>]*>", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5)))
        .Trim();

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
        .Matches(StripComments(Newlines(css)), @"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(5))
        .Select(m => new CssRule(m.Groups["sel"].Value.Trim(), m.Groups["body"].Value))
        .ToList();

    /// <summary>
    /// One newline, whatever the checkout uses.
    ///
    /// <para>A selector list is one selector per line, so a rule's prelude carries the file's own
    /// line ending — and the assertions above name those preludes as C# literals, which carry
    /// <c>\n</c>. This repository is <c>* text=auto</c> with <c>core.autocrlf=true</c>, so a Windows
    /// working tree holds CRLF and every multi-line selector lookup missed. It passed for as long as
    /// the file happened to be sitting in the tree with LF endings, and failed the first time git
    /// normalised it — which is to say the suite was green because of how a file had been written
    /// rather than because of what it said.</para>
    /// </summary>
    private static string Newlines(string css) => css.Replace("\r\n", "\n", StringComparison.Ordinal);

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
