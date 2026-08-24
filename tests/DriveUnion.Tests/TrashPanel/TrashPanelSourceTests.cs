using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// The screens this slice added, held to the rule <c>MigratedScreensTests</c> holds the rest of the
/// panel to: a screen says nothing of its own, it names an entry in <c>UiText</c>.
///
/// <para>It is the same assertion in a second place because that file's list of migrated folders is
/// not this slice's to edit, and a screen nobody's guard covers decays one literal at a time —
/// somebody adds a label in Persian because that is what the file around it looks like, it renders
/// perfectly in Persian, and the English panel ships a Persian word.</para>
///
/// <para>The layout rules need no copy: <c>PanelLayoutTests</c> walks every <c>.cshtml</c> under
/// <c>Views/</c> bar three folders, so both of these screens are already inside its notice, colour
/// and control-box guards.</para>
/// </summary>
public class TrashPanelSourceTests
{
    /// <summary>
    /// Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.
    /// Escaped rather than written out, like <c>MigratedScreensTests</c>: U+200C is invisible in an
    /// editor, and a class that had lost it would still compile and still match almost everything.
    /// </summary>
    private const string PersianCharacter = "[؀-ۿ‌]";

    /// <summary>
    /// Everything this slice writes, except its own catalogue file — <c>UiText.Trash.cs</c> is the
    /// one place a Persian literal belongs, which is the entire point of it.
    /// </summary>
    public static TheoryData<string> Sources() => new(
        "src/DriveUnion.Web/Views/Trash/Index.cshtml",
        "src/DriveUnion.Web/Views/Settings/Index.cshtml",
        "src/DriveUnion.Web/Controllers/TrashController.cs",
        "src/DriveUnion.Web/Controllers/SettingsController.cs",
        "src/DriveUnion.Web/Models/TrashViewModels.cs",
        "src/DriveUnion.Web/Models/SettingsViewModels.cs",
        "src/DriveUnion.Web/Infrastructure/TrashPanelServices.cs",
        "src/DriveUnion.Web/Infrastructure/ShellContext.cs");

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_screen_this_slice_added_holds_no_persian_outside_its_comments(string relativePath)
    {
        var file = new FileInfo(Path.Combine(RepositoryRoot().FullName, relativePath));

        Assert.True(file.Exists, $"{relativePath} is listed here and does not exist.");

        var code = WithoutComments(File.ReadAllText(file.FullName));

        var stray = Regex.Match(code, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5));

        stray.Success.Should().BeFalse(
            "{0} has a Persian literal in it at index {1}, near «{2}» — a screen says nothing of its "
            + "own, it names an entry in UiText",
            relativePath,
            stray.Index,
            Excerpt(code, stray.Index));
    }

    /// <summary>
    /// The catalogue file really is the exception, and really does hold the words.
    ///
    /// <para>Without this, the theory above would pass just as happily on a slice whose screens had
    /// no words at all — and <c>UiText.Trash.cs</c> being absent from that list would look like a
    /// decision rather than the oversight it would be.</para>
    /// </summary>
    [Fact]
    public void The_catalogue_file_is_where_the_words_are()
    {
        var catalogue = File.ReadAllText(Path.Combine(
            RepositoryRoot().FullName,
            "src/DriveUnion.Web/Localization/UiText.Trash.cs"));

        Regex.Matches(catalogue, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Count.Should().BeGreaterThan(100, "three screens' worth of Persian lives in this file");
    }

    /// <summary>
    /// Razor comments and single-line C# comments, removed — the same two forms
    /// <c>MigratedScreensTests</c> strips, and for the same reason: these files explain the product
    /// in the product's own language and must go on doing so. The rule is about what the page says.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var razorless = Regex.Replace(
            source,
            @"@\*.*?\*@",
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        return Regex.Replace(
            razorless,
            @"^\s*//.*$",
            string.Empty,
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));
    }

    private static string Excerpt(string text, int index)
    {
        var start = Math.Max(0, index - 20);
        var length = Math.Min(60, text.Length - start);

        return text.Substring(start, length).ReplaceLineEndings(" ");
    }

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
}
