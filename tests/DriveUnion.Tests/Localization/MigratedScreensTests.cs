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
    ];

    /// <summary>
    /// The public download page, which resolves its own language in <c>PublicDownloadController</c>
    /// and writes its pairs inline in the view. That is a different mechanism and it is left alone
    /// on purpose — Localization/README.md says what folding it in costs and why it is its own
    /// change. Until then this file is the boundary between the two, and it is named here rather
    /// than silently skipped.
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
