using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// Two properties that are true of the source rather than of a run, and that nothing else can see.
///
/// <para>M5 §10 left exactly one seam: a tenant's cap has one writer. A second writer would not fail
/// a test — it would work perfectly, write no history row, and be discovered when a customer asked
/// why their quota changed and nobody could answer. The only place that shows up is the source.</para>
/// </summary>
public class PlanSourceRulesTests
{
    /// <summary>Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.</summary>
    private const string PersianCharacter = "[؀-ۿ‌]";

    /// <summary>
    /// The four numbers every check compares against. A write to any of them, anywhere but the one
    /// command, is a customer's ceiling moving with no history row behind it.
    ///
    /// <para>Both shapes a write can take: an assignment through a reference, and EF's
    /// <c>ExecuteUpdate</c>, which goes round the change tracker and would leave no trace in a
    /// <c>SaveChanges</c> anybody was watching.</para>
    /// </summary>
    /// <remarks>
    /// <c>(?![=&gt;])</c> so that <c>==</c> is a comparison and <c>=&gt;</c> is a switch arm. Without
    /// the second, every <c>QuotaField.MaxMembers =&gt; …</c> in a perfectly innocent projection reads
    /// as a write.
    /// </remarks>
    private const string EffectiveLimits =
        @"\.(StorageQuotaBytes|MaxFileBytes|MonthlyEgressBytes|MaxMembers)\s*=(?![=>])"
        + @"|SetProperty\([^)]*\.(StorageQuotaBytes|MaxFileBytes|MonthlyEgressBytes|MaxMembers)";

    /// <summary>The counter. Reserve, settle, release — and nothing else touches it.</summary>
    private const string UsageCounter =
        @"StorageUsedBytes\s*=(?![=>])|SetProperty\([^)]*StorageUsedBytes";

    private const string PlanService = "src/DriveUnion.Infrastructure/Plans/TenantPlanService.cs";

    private const string StorageMeter = "src/DriveUnion.Infrastructure/Plans/TenantStorageMeter.cs";

    /// <summary>
    /// The screens this slice added. They are guarded here rather than by
    /// <c>MigratedScreensTests</c> only because that file belongs to the localisation slice; the rule
    /// is the same one, and the two lists should be merged when either is next opened.
    /// </summary>
    private static readonly string[] Screens =
    [
        "src/DriveUnion.Web/Controllers/PlansController.cs",
        "src/DriveUnion.Web/Models/PlansViewModels.cs",
        "src/DriveUnion.Web/Views/Plans/Index.cshtml",
        "src/DriveUnion.Web/Views/Plans/Operator.cshtml",
        "src/DriveUnion.Web/Views/Plans/OperatorTenant.cshtml",
        "src/DriveUnion.Web/Views/Plans/Tier.cshtml",
        "src/DriveUnion.Web/Views/Plans/Reapply.cshtml",
    ];

    [Fact]
    public void Only_one_command_writes_a_tenants_effective_limits()
    {
        var offenders = SourcesAssigning(EffectiveLimits)
            .Where(file => file != PlanService)
            .ToList();

        offenders.Should().BeEmpty(
            "a tenant's four numbers have exactly one writer, and a second one would move a "
            + "customer's ceiling with no history row behind it — see ITenantPlanService");
    }

    [Fact]
    public void Only_the_meter_writes_the_usage_counter()
    {
        var offenders = SourcesAssigning(UsageCounter)
            .Where(file => file != StorageMeter)
            .ToList();

        offenders.Should().BeEmpty(
            "reserve, settle and release are the only three transitions, and a fourth writer is how "
            + "a tenant's counter and their files stop agreeing");
    }

    /// <summary>
    /// Without this, a rename that moved the writers somewhere else would leave both guards above
    /// passing over a file list that matches nothing.
    /// </summary>
    [Fact]
    public void The_two_writers_are_really_the_ones_being_read()
    {
        SourcesAssigning(EffectiveLimits).Should().Contain(PlanService);
        SourcesAssigning(UsageCounter).Should().Contain(StorageMeter);
    }

    public static TheoryData<string> ScreenFiles()
    {
        var data = new TheoryData<string>();

        foreach (var screen in Screens) data.Add(screen);

        return data;
    }

    [Theory]
    [MemberData(nameof(ScreenFiles))]
    public void A_plan_screen_holds_no_persian_outside_its_comments(string relativePath)
    {
        var full = Path.Combine(RepositoryRoot().FullName, relativePath);

        File.Exists(full).Should().BeTrue($"{relativePath} is listed as a plan screen and does not exist");

        var code = WithoutComments(File.ReadAllText(full));

        var stray = Regex.Match(code, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5));

        stray.Success.Should().BeFalse(
            "{0} has a Persian literal in it at index {1} — a screen says nothing of its own, it "
            + "names an entry in UiText, and the panel is bilingual",
            relativePath,
            stray.Index);
    }

    private static List<string> SourcesAssigning(string pattern)
    {
        var root = RepositoryRoot();
        var source = new DirectoryInfo(Path.Combine(root.FullName, "src"));

        Assert.True(source.Exists, "The source tree is not where this test expects it.");

        var hits = new List<string>();

        foreach (var file in source.EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

            // Generated build output, and the migrations EF writes — neither is anybody's decision.
            if (relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (relative.Contains("/Migrations/", StringComparison.Ordinal)) continue;

            var code = WithoutComments(File.ReadAllText(file.FullName));

            if (Regex.IsMatch(code, pattern, RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                hits.Add(relative);
            }
        }

        return hits;
    }

    /// <summary>Razor comments and single-line C# comments, removed — including <c>///</c> doc comments.</summary>
    private static string WithoutComments(string source)
    {
        var razorless = Regex.Replace(
            source, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        return Regex.Replace(
            razorless, @"^\s*//.*$", string.Empty, RegexOptions.Multiline, TimeSpan.FromSeconds(5));
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
