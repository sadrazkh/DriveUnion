using System.Reflection;
using System.Text.RegularExpressions;
using DriveUnion.Core.Application;
using FluentAssertions;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// Two rules about this slice's own source, both of which a rendered page can only catch by
/// accident.
///
/// <para>The first is the localisation rule <c>MigratedScreensTests</c> applies to every screen that
/// has been migrated. <c>Views/Dashboard</c> is not on that file's list and that file is not this
/// slice's to edit, so the same rule is applied here, to this slice's files, from the folder that
/// owns them.</para>
///
/// <para>The second is M1 §1.4 asserted against the shape of the data rather than against the
/// markup. A screen test can only prove that today's view does not print the pool; this proves that
/// tomorrow's cannot, because the record it is built from carries nothing to print.</para>
/// </summary>
public class DashboardSourceTests
{
    /// <summary>
    /// Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.
    /// Escaped rather than written out: U+200C is invisible in an editor, and a class that had lost
    /// it would still compile and still match almost everything.
    /// </summary>
    private const string PersianCharacter = "[\u0600-\u06FF\u200C]";

    /// <summary>Everything this slice added that a reader ever sees words from.</summary>
    private static readonly string[] Screens =
    [
        "src/DriveUnion.Web/Views/Dashboard/Customer.cshtml",
        "src/DriveUnion.Web/Views/Dashboard/Operator.cshtml",
        "src/DriveUnion.Web/Models/DashboardViewModels.cs",
        "src/DriveUnion.Web/Controllers/HomeController.cs",
    ];

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();

        foreach (var screen in Screens) data.Add(screen);

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_dashboard_says_nothing_of_its_own(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot().FullName, relativePath);

        File.Exists(path).Should().BeTrue($"{relativePath} is what this test is about");

        var code = WithoutComments(File.ReadAllText(path));

        var stray = Regex.Match(code, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5));

        stray.Success.Should().BeFalse(
            "{0} has a Persian literal in it at index {1}, near «{2}» — a screen names an entry in "
            + "UiText rather than saying anything itself, or the English panel cannot say it",
            relativePath,
            stray.Index,
            Excerpt(code, stray.Index));
    }

    /// <summary>
    /// <b>The customer's dashboard cannot name the operator's pool, whatever a view does.</b>
    ///
    /// <para>M1 §1.4: the Google accounts are the operator's, and a customer must never learn which
    /// one holds their file nor that a pool exists. The screen test asserts that the rendered page
    /// carries none of it; this asserts that there is none of it to carry — every field reachable
    /// from <see cref="CustomerDashboard"/> is about the workspace, and adding one that is not is a
    /// red test rather than a leak nobody notices.</para>
    /// </summary>
    [Theory]
    [InlineData("Account")]
    [InlineData("Drive")]
    [InlineData("Google")]
    [InlineData("Pool")]
    [InlineData("Folder")]
    [InlineData("Daily")]
    public void Nothing_a_customer_can_be_handed_names_the_pool(string forbidden)
    {
        var offenders = Reachable(typeof(CustomerDashboard))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            "«{0}» is the operator's business; a customer's dashboard must not be able to carry it",
            forbidden);
    }

    /// <summary>
    /// The positive control. Without it the theory above would pass on a panel where
    /// <see cref="OperatorDashboard"/> had also been emptied — or where reflection was reaching
    /// nothing at all.
    /// </summary>
    [Fact]
    public void The_operators_own_dashboard_does_carry_the_pool()
    {
        var names = Reachable(typeof(OperatorDashboard))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToList();

        names.Should().Contain("Accounts");
        names.Should().Contain("PoolTotalBytes");
        names.Should().Contain("Email", "an account card names the operator's own address");
    }

    /// <summary>
    /// Every record type a caller can walk to from <paramref name="root"/>, including the element
    /// types of its lists. A guard that only read the top record would miss a leak one hop down,
    /// which is exactly where a file's Google account would sit.
    /// </summary>
    private static List<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();

        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();

            if (!seen.Add(type)) continue;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var candidate in Unwrap(property.PropertyType))
                {
                    // Only this product's own types. Walking into string or DateTimeOffset would
                    // reach the framework and prove nothing about anything.
                    if (candidate.Namespace?.StartsWith("DriveUnion", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        return [.. seen];
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments()) yield return argument;
        }
    }

    /// <summary>
    /// Razor comments and single-line C# comments, removed — the same two forms
    /// <c>MigratedScreensTests</c> strips, and for the same reason: the comments in these files
    /// explain the product in the product's own language and must keep doing so. The rule is about
    /// what the page says, not about what the source discusses.
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
