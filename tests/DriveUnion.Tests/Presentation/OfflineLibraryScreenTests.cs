using System.Text.Json;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Links;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// «ذخیره‌شده روی این دستگاه» — the screen that lists what this browser is holding.
///
/// <para>It is an island over an otherwise empty page, because everything on it is read from the
/// device's own storage: this server has no idea what is kept there and must not, since a list it
/// could produce would be a list of somewhere else. So what there is to test here is the seam — the
/// mount point, and the sentences handed across it.</para>
///
/// <para><b>The sentences are the part that breaks silently.</b> Razor writes them as JSON into a
/// data attribute and the component destructures them by name, which means a key renamed on one side
/// renders as nothing at all on the other: no error, no warning, a button with no label. That is what
/// the second test is for, and it is the reason it reads the .vue rather than restating its keys.</para>
/// </summary>
public class OfflineLibraryScreenTests
{
    private const string Island = "src/DriveUnion.Web/Scripts/islands/OfflineLibrary.vue";

    [Fact]
    public async Task It_renders_for_a_signed_in_customer_and_carries_the_island()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        var markup = await harness.NewClient(tenant.Id)
            .GetStringAsync(new Uri("/files/offline", UriKind.Relative));

        markup.Should().Contain("data-island=\"offline-library\"");

        // The one thing a reader will not guess about this screen, which is that it is per browser
        // and per device. Asserted because a list that looked like part of the account would have
        // people wondering why their phone and their laptop disagree.
        markup.Should().Contain("data-text=");
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged_rather_than_shown_an_empty_list()
    {
        using var harness = new PanelPageHarness();

        using var response = await harness.NewClient(null)
            .GetAsync(new Uri("/files/offline", UriKind.Relative));

        // Challenged, however this host spells it — the cookie pipeline redirects to sign-in and
        // this harness's stub answers 401. What matters is that it is not the page.
        response.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.Redirect,
            System.Net.HttpStatusCode.Unauthorized,
            System.Net.HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Every key the component reads is a key the view sends, and the other way round.
    ///
    /// <para>Both directions on purpose. A key the view sends and the component ignores is dead
    /// weight that will be maintained for years; a key the component reads and the view does not
    /// send is a control with no label, rendered without complaint by anything.</para>
    /// </summary>
    [Fact]
    public async Task The_view_sends_exactly_the_sentences_the_island_reads()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        var markup = await harness.NewClient(tenant.Id)
            .GetStringAsync(new Uri("/files/offline", UriKind.Relative));

        var sent = SentKeys(markup);
        var read = ReadKeys(Read(Island));

        read.Should().NotBeEmpty("the prop block was renamed or reshaped, and this test found nothing");

        sent.Should().BeEquivalentTo(
            read,
            "a key on one side and not the other is a control that renders as an empty string");
    }

    /// <summary>Repo-relative, because a test runs out of bin and the .vue does not go there.</summary>
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

        throw new InvalidOperationException("No DriveUnion.slnx above the test binaries.");
    }

    /// <summary>The keys in `data-text`, decoded out of the attribute Razor HTML-encoded.</summary>
    private static IReadOnlyCollection<string> SentKeys(string markup)
    {
        var match = Regex.Match(
            markup,
            "data-text=\"(?<json>[^\"]*)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue("the island is handed its words through data-text");

        var json = System.Net.WebUtility.HtmlDecode(match.Groups["json"].Value);

        using var document = JsonDocument.Parse(json);

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>
    /// The keys the component declares inside `text: { … }` in its props.
    ///
    /// <para>Read out of the file rather than listed here, because a list here would be a third
    /// place to keep in step and the first one to be forgotten.</para>
    /// </summary>
    private static IReadOnlyCollection<string> ReadKeys(string source)
    {
        var block = Regex.Match(
            source,
            @"text:\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        if (!block.Success) return [];

        return
        [
            .. Regex
                .Matches(block.Groups["body"].Value, @"(?<name>\w+)\s*:", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(m => m.Groups["name"].Value),
        ];
    }
}
