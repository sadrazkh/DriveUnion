using System.Net;
using System.Text.RegularExpressions;

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// A very small reader over the panel's rendered markup, for the screens this slice added.
///
/// <para>It is a second copy of what <c>PanelLayoutTests</c> and <c>PanelScreenLanguageTests</c>
/// already do, and deliberately so: those files hold lists of the screens they cover, this slice's
/// two screens are not on them, and neither file is this slice's to edit. The assertions are the
/// same rules, applied to the same markup, from the folder that owns the screens.</para>
///
/// <para><b>Decode before asserting on words, never before asserting on attributes.</b> Razor's
/// encoder writes everything outside Basic Latin as <c>&amp;#x641;…</c>, so a Persian sentence is
/// not in the raw markup at all and <c>Contain</c> against it passes and fails for the wrong
/// reasons. Decoding also turns <c>&amp;quot;</c> into a quote, which is why the direction checks
/// read the raw markup.</para>
/// </summary>
public static class PanelMarkup
{
    /// <summary>An element with no element inside it: its attributes as written, and its text.</summary>
    public sealed record Leaf(string Attributes, string Text);

    /// <summary>The words a reader would see, with the encoder's entities put back.</summary>
    public static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>The page's own content, decoded — everything the shell draws is outside it.</summary>
    public static string MainContent(string html)
    {
        var match = Regex.Match(
            html,
            "<main class=\"app-content\">(.*)</main>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The page rendered no <main class=\"app-content\"> region.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>The shell's sidebar, raw. The capacity card and the nav both live in it.</summary>
    public static string Sidebar(string html)
    {
        var match = Regex.Match(
            html,
            "<nav class=\"app-sidebar\".*?</nav>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The page rendered no <nav class=\"app-sidebar\">.");

        return match.Value;
    }

    /// <summary>The one <c>.dtable</c> on a page, with its tracks and the columns it draws.</summary>
    public static (string Cols, int HeadCells) SingleTable(string html)
    {
        var table = Regex.Match(
            html,
            @"class=""dtable""\s+style=""(?<cols>--cols:[^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(table.Success, "The page rendered no .dtable carrying a --cols.");

        // A header cell holds text and never an element, which is what lets this stop at the end of
        // the header instead of walking into the first row.
        var head = Regex.Match(
            html[table.Index..],
            @"<div class=""dtable-head"">(?<cells>(?:\s*<div[^>]*>[^<]*</div>)+)\s*</div>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(head.Success, "The .dtable has no header to compare its tracks with.");

        return (
            table.Groups["cols"].Value,
            Regex.Matches(head.Groups["cells"].Value, "<div", RegexOptions.None, TimeSpan.FromSeconds(5)).Count);
    }

    /// <summary>
    /// Top-level tracks in a <c>--cols</c>, so <c>minmax(var(--name-min), 2.4fr)</c> counts once and
    /// the space inside its parentheses is not mistaken for the space between two tracks.
    /// </summary>
    public static int TrackCount(string cols)
    {
        ArgumentNullException.ThrowIfNull(cols);

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

    /// <summary>
    /// Every leaf on the page whose whole text is a byte quantity: <c>5 GB</c>, <c>18.4 MB</c>,
    /// <c>0 B / 202 MB</c>.
    ///
    /// <para>Anchored at both ends so a sentence that merely mentions a size does not match — a
    /// sentence cannot be fixed from a view anyway, because no part of a string can be isolated from
    /// the rest of the same string.</para>
    /// </summary>
    public static List<Leaf> LatinReadouts(string html) =>
    [
        .. Regex
            .Matches(
                html,
                @"<(?<tag>div|span)(?<attrs>[^>]*)>(?<text>[^<>]*)</\k<tag>>",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(m => new Leaf(m.Groups["attrs"].Value, m.Groups["text"].Value))
            .Where(leaf => Regex.IsMatch(
                leaf.Text,
                @"^\s*[\d.,]+\s+[A-Za-z]{1,3}(\s*/\s*[\d.,]+\s+[A-Za-z]{1,3})?\s*$",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))),
    ];
}
