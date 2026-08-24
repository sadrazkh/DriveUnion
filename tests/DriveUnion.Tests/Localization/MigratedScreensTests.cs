using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// The screens this slice migrated, kept migrated.
///
/// A localised panel decays one literal at a time: somebody adds a label to the shell in Persian
/// because that is what the file around it looks like, it renders perfectly in both languages, and
/// nothing fails. This reads the source of the screens that are done and refuses a Persian word
/// outside a comment — which is not a style rule, it is the only signal that a string escaped the
/// catalogue.
///
/// It is deliberately scoped to what has been migrated. The rest of the panel is still Persian
/// literals everywhere and is the next agent's work; adding a folder to <see cref="Migrated"/> is
/// how that work reports itself finished.
/// </summary>
public class MigratedScreensTests
{
    /// <summary>Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.</summary>
    private const string PersianCharacter = "[\u0600-\u06FF\u200C]";

    /// <summary>Grown as screens are migrated. Paths are relative to the repository root.</summary>
    private static readonly string[] Migrated =
    [
        "src/DriveUnion.Web/Views/Shared",
        "src/DriveUnion.Web/Areas/Identity",
        "src/DriveUnion.Web/Localization",
        "src/DriveUnion.Web/Views/Home",
        "src/DriveUnion.Web/Views/Files",
        "src/DriveUnion.Web/Views/Links",
        "src/DriveUnion.Web/Views/Accounts",
        "src/DriveUnion.Web/Views/Design",
    ];

    /// <summary>
    /// The other half of a screen, named one file at a time.
    ///
    /// A sentence a controller puts in <c>TempData</c> and a status word a view model maps an enum
    /// to are as much a part of the screen as its markup — «فایل حذف شد.» never appeared in a
    /// <c>.cshtml</c> at all. They are listed individually rather than by folder because
    /// <c>Controllers/</c> and <c>Models/</c> also hold the Telegram surface, which is another
    /// agent's and is migrated after it settles.
    ///
    /// <c>Models/DisplayFormats.cs</c> is deliberately not among them. It is the other half of
    /// <c>PersianDigits</c> — the implementation of the numeral rule rather than a screen — and its
    /// Persian output is pinned character for character by <c>PersianDigitsTests</c>.
    /// </summary>
    private static readonly string[] MigratedSources =
    [
        "src/DriveUnion.Web/Controllers/HomeController.cs",
        "src/DriveUnion.Web/Controllers/FilesController.cs",
        "src/DriveUnion.Web/Controllers/FilesApiController.cs",
        "src/DriveUnion.Web/Controllers/LinksController.cs",
        "src/DriveUnion.Web/Controllers/AccountsController.cs",
        "src/DriveUnion.Web/Controllers/UploadsController.cs",
        "src/DriveUnion.Web/Controllers/ShareLinksController.cs",
        "src/DriveUnion.Web/Controllers/DesignController.cs",
        "src/DriveUnion.Web/Models/FilesViewModels.cs",
        "src/DriveUnion.Web/Models/LinksViewModels.cs",
        "src/DriveUnion.Web/Models/AccountsViewModels.cs",
    ];

    /// <summary>
    /// The public download page, which resolves its own language in <c>PublicDownloadController</c>
    /// and writes its pairs inline in the view. That is a different mechanism and it is left alone
    /// on purpose — Localization/README.md says what folding it in costs and why it is its own
    /// change. Until then this file is the boundary between the two, and it is named here rather
    /// than silently skipped.
    ///
    /// <c>Views/Public/**</c> is the rest of it and is absent from <see cref="Migrated"/> for the
    /// same reason: its layout is <c>Views/Shared/_PublicLayout.cshtml</c>, which builds the
    /// document's language, its FA/EN control and its <c>hreflang</c> alternates from
    /// <c>ViewData["Lang"]</c> rather than from <c>PanelCulture</c>. Folding the two views in while
    /// the layout around them still answers to the other mechanism would leave the page saying one
    /// thing in its card and another in its chrome.
    /// </summary>
    private static readonly string[] NotMigrated =
    [
        "_PublicLayout.cshtml",
    ];

    /// <summary>
    /// <c>UiText</c> and the catalogue around it are the one place a Persian literal belongs,
    /// which is the entire point of them.
    /// </summary>
    private static readonly string[] TheCatalogue =
    [
        "UiText.cs",
        "DriveUnionIdentityErrorDescriber.cs",
    ];

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();

        foreach (var file in MigratedFiles()) data.Add(file);

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_migrated_screen_holds_no_persian_outside_its_comments(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot().FullName, relativePath));

        var code = WithoutComments(source);

        var stray = Regex.Match(code, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5));

        stray.Success.Should().BeFalse(
            "{0} has a Persian literal in it at index {1}, near «{2}» — a migrated screen says nothing "
            + "of its own, it names an entry in UiText",
            relativePath,
            stray.Index,
            Excerpt(code, stray.Index));
    }

    /// <summary>
    /// The list is really being read. Without this, a wrong root or a renamed folder would leave the
    /// theory above with no cases and the whole guard passing on nothing.
    /// </summary>
    [Fact]
    public void Every_migrated_screen_is_actually_being_read()
    {
        var files = MigratedFiles();

        files.Should().Contain("src/DriveUnion.Web/Views/Shared/_Layout.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Areas/Identity/Views/Account/Login.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Areas/Identity/Views/Account/Setup.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Areas/Identity/Views/Account/Logout.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Areas/Identity/Views/Account/AccessDenied.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Areas/Identity/Controllers/AccountController.cs");
        files.Should().NotContain("src/DriveUnion.Web/Views/Shared/_PublicLayout.cshtml");

        // The screens this slice migrated, one from each folder, plus the two halves a folder list
        // on its own would miss: a controller's TempData sentence and a view model's status word.
        files.Should().Contain("src/DriveUnion.Web/Views/Files/Index.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Views/Links/Index.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Views/Accounts/_GoogleSetup.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Views/Design/_Gallery.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Views/Home/Error.cshtml");
        files.Should().Contain("src/DriveUnion.Web/Controllers/AccountsController.cs");
        files.Should().Contain("src/DriveUnion.Web/Models/LinksViewModels.cs");

        // And the two that are deliberately outside it, so "not listed" stays a decision rather
        // than an oversight.
        files.Should().NotContain("src/DriveUnion.Web/Models/DisplayFormats.cs");
        files.Should().NotContain("src/DriveUnion.Web/Views/Public/Download.cshtml");
    }

    private static List<string> MigratedFiles()
    {
        var root = RepositoryRoot();
        var files = new List<string>();

        foreach (var folder in Migrated)
        {
            var directory = new DirectoryInfo(Path.Combine(root.FullName, folder));

            Assert.True(directory.Exists, $"{folder} is listed as migrated and does not exist.");

            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension is not (".cshtml" or ".cs")) continue;
                if (NotMigrated.Contains(file.Name)) continue;
                if (TheCatalogue.Contains(file.Name)) continue;

                files.Add(Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'));
            }
        }

        foreach (var source in MigratedSources)
        {
            Assert.True(
                File.Exists(Path.Combine(root.FullName, source)),
                $"{source} is listed as migrated and does not exist.");

            files.Add(source);
        }

        return files;
    }

    /// <summary>
    /// Razor comments and single-line C# comments, removed.
    ///
    /// The comments in these files explain the product in the product's own language and must keep
    /// doing so — the rule is about what the page says, not about what the source discusses. Only
    /// these two forms are stripped because only these two are used here; a block comment with
    /// Persian in it would trip this test, which is a fair price for not writing a C# parser.
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

    /// <summary>
    /// The checkout, found by walking up from the test assembly until the solution file appears.
    /// The artifacts path is configurable, so the depth is not something to count.
    /// </summary>
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
