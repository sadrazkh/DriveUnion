using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Plans;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The menu offers only what the reader can open.
///
/// <para>Two of the panel's primary nav items were drawn for anybody signed in — «فایل‌ها» and
/// «لینک‌های اشتراک» — and both controllers carry <c>[Authorize(Policy = Tenant)]</c>. An operator
/// with no workspace was shown both, and both answered 403. The header's search box was the same
/// defect wearing the comp's wording: it said «جست‌وجو در همه‌ی اکانت‌ها» to an operator and
/// submitted a GET to <c>/files</c>.</para>
///
/// <para>Nothing failed. The policies did exactly what they are for and the shell kept pointing at
/// them, which is the shape of every version of this bug: the fact the route authorises on and the
/// fact the sidebar draws on are the same fact, held in two places. The layout already knew — it
/// hides «زباله‌دان» on <c>hasWorkspace</c> and writes the reasoning beside «صف انتقال» — and the
/// reasoning was simply not applied to its neighbours.</para>
///
/// <para>So this test does not name items. It reads whatever the shell drew and presses all of it.</para>
/// </summary>
public class ShellNavigationTests
{
    /// <summary>The sidebar, so a link in the page body is not mistaken for a menu item.</summary>
    private static readonly Regex Sidebar = new(
        """<nav class="app-sidebar".*?</nav>""",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    // Neither pattern needs a closing quote: the character classes already stop at one.
    private static readonly Regex Href = new(
        """<a\b[^>]*\shref="(?<href>/[^"]*)""",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    private static readonly Regex SearchAction = new(
        """<form class="search"[^>]*\saction="(?<action>[^"]+)""",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task An_operator_with_no_workspace_is_offered_nothing_that_refuses_them()
    {
        await using var harness = new PlanPageHarness();
        harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenantId: null, asOperator: true);

        await EveryMenuItemOpensAsync(client, "an operator with no workspace");
    }

    [Fact]
    public async Task A_customer_is_offered_nothing_that_refuses_them()
    {
        await using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);

        // The other side of the same rule, and the reason this is not written as «hide these two
        // from operators»: a gate that is too wide takes the panel away from the people it is for.
        await EveryMenuItemOpensAsync(client, "a customer");
    }

    [Fact]
    public async Task The_search_box_is_drawn_only_for_somebody_who_has_a_library_to_search()
    {
        await using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var operatorClient = harness.NewClient(tenantId: null, asOperator: true);
        using var customerClient = harness.NewClient(tenant.Id);

        var operatorShell = await operatorClient.GetStringAsync("/");
        var customerShell = await customerClient.GetStringAsync("/files");

        // It is a GET to /files whoever it is drawn for, so «who may see the box» and «who may open
        // /files» have to be one answer.
        SearchAction.IsMatch(operatorShell).Should().BeFalse(
            "the box submits to /files, which is behind the tenant policy");

        var customerAction = SearchAction.Match(customerShell);
        customerAction.Success.Should().BeTrue("a customer has files to search");
        customerAction.Groups["action"].Value.Should().BeEquivalentTo("/Files");
    }

    /// <summary>Presses every link the sidebar drew and reports the ones that answered a refusal.</summary>
    private static async Task EveryMenuItemOpensAsync(HttpClient client, string who)
    {
        var shell = await client.GetStringAsync("/");

        var nav = Sidebar.Match(shell);
        nav.Success.Should().BeTrue("the shell draws a sidebar for anyone signed in");

        var hrefs = Href.Matches(nav.Value)
            .Select(m => m.Groups["href"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        hrefs.Should().NotBeEmpty($"the menu drawn for {who} has items in it");

        var refused = new List<string>();

        foreach (var href in hrefs)
        {
            using var response = await client.GetAsync(new Uri(href, UriKind.Relative));

            // A redirect is not a refusal — it is the panel sending somebody somewhere. Only the two
            // answers that mean «not for you» count, because they are what a reader sees as a menu
            // item that does not work.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                refused.Add($"{href} → {(int)response.StatusCode}");
            }
        }

        refused.Should().BeEmpty(
            $"every item the shell draws for {who} is an item they can open; a dead one teaches "
            + "them to distrust the whole menu");
    }
}
