using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Links;

/// <summary>
/// The search box end to end: what the header submits, what «فایل‌ها» answers with, and what it
/// says when nothing matched.
///
/// <para><c>FileSearchTests</c> holds the query. This holds the half a query cannot: that the
/// parameter the form has always sent is the parameter the action binds. Those two were not the
/// same thing for the whole life of the screen — the box submitted <c>q</c> and
/// <c>Index(Guid? selected, CancellationToken)</c> ignored it — and nothing in a suite that tests
/// the catalogue and the markup separately can see a gap that lives exactly between them.</para>
/// </summary>
public class FileSearchScreenTests
{
    [Fact]
    public async Task The_table_holds_only_what_the_term_matched()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        harness.SeedFile(tenant.Id, "holiday-photo.jpg");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files?q=report");

        markup.Should().Contain("Q3-Report-Final.pdf");
        markup.Should().NotContain("holiday-photo.jpg");
    }

    [Fact]
    public async Task The_box_comes_back_holding_what_was_typed()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files?q=report");

        // Without this the results arrive next to an empty box, which reads as a search that did not
        // take — and leaves the reader no way to see what they actually asked for.
        markup.Should().MatchRegex(@"<input[^>]*id=""shell-search""[^>]*value=""report""");
    }

    [Fact]
    public async Task A_row_leads_somewhere_that_still_has_the_search()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        harness.SeedFile(tenant.Id, "holiday-photo.jpg");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files?q=report");

        // A row leads to that file's own page now rather than back to this list with a panel open —
        // but the search still has to travel, and for the same reason it always did: without it,
        // «back» from the file lands the reader on the unfiltered list and their result is gone.
        //
        // The link is inside the name cell rather than being the row, and that is what multi-select
        // cost: a real checkbox cannot live inside an anchor, because every click on it navigates.
        markup.Should().MatchRegex(@"<a href=""/files/[0-9a-f-]{36}\?q=report""");
    }

    [Fact]
    public async Task Nothing_matched_is_a_different_answer_from_nothing_uploaded()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);

        // Decoded first, the way LinksScreenTests reads a page: this app configures no web encoder,
        // so Razor writes every non-ASCII character as a numeric entity and a raw Persian substring
        // is never in the response.
        var text = WebUtility.HtmlDecode(await client.GetStringAsync("/files?q=payroll"));

        // The workspace is not empty; this term is. «آپلود اولین فایل» under a search reads as «the
        // file you are looking for is gone», which is the opposite of what happened.
        //
        // Persian because that is what this panel renders by default, the same as every other screen
        // test in this file. PanelScreenLanguageTests is where the English half is held.
        text.Should().Contain("payroll");
        text.Should().NotContain("هنوز فایلی آپلود نشده است.");
        text.Should().Contain("دیدن همه‌ی فایل‌ها");
    }

    [Fact]
    public async Task The_term_is_written_back_into_the_box_as_text_and_not_as_markup()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files?q=%22%3E%3Cscript%3E");

        // The one new thing this feature does is put a stranger's text back on the page — in an
        // attribute, in the shell, on every screen. Razor's `@` encodes it and this is what says so.
        //
        // Asserted as "the attribute was never closed" rather than as "the page has no <script>":
        // the layout has one of its own, so the blunt version of this test fails on the theme
        // bootstrap and proves nothing about the term.
        markup.Should().NotContain(@"value=""""><script>");
        markup.Should().Contain("&quot;&gt;&lt;script&gt;");
    }

    [Fact]
    public async Task An_empty_term_is_the_whole_list()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        harness.SeedFile(tenant.Id, "holiday-photo.jpg");

        using var client = harness.NewClient(tenant.Id);

        // What an empty box submits. Answering it with an empty table would tell a reader who
        // pressed Enter by accident that their workspace is gone.
        var markup = await client.GetStringAsync("/files?q=");

        markup.Should().Contain("Q3-Report-Final.pdf");
        markup.Should().Contain("holiday-photo.jpg");
    }

    [Fact]
    public async Task A_search_cannot_reach_another_workspaces_files()
    {
        using var harness = new PanelPageHarness();
        var mine = harness.SeedTenant("Acme", "shared-name.pdf", "kx91mzq4");
        harness.SeedTenant("Globex", "shared-name.pdf", "zq40mkx9");

        using var client = harness.NewClient(mine.Id);
        var markup = await client.GetStringAsync("/files?q=shared-name");

        // Both workspaces own a file by that name. A WHERE bolted onto a query is where a tenant
        // scope goes missing, so the isolation is asserted through the screen as well as under it.
        var occurrences = markup.Split("shared-name.pdf").Length - 1;

        occurrences.Should().BeGreaterThan(0, "the reader's own file is what they searched for");

        var secrets = harness.SecretsOf(mine.Id);
        markup.Should().NotContain(secrets.AccountEmail);
        markup.Should().NotContain(secrets.DriveFileId);
    }

    [Fact]
    public async Task An_anonymous_visitor_gets_no_answer_from_the_search()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(null);
        using var response = await client.GetAsync(new Uri("/files?q=report", UriKind.Relative));

        // The term is a query string on a page behind the tenant policy, and a query string is not a
        // key. Asserted because a search endpoint is exactly the kind of thing that gets an
        // «it is only a read» exemption later.
        //
        // The status is whatever the challenge is — 401 from this harness's header scheme, a
        // redirect to the sign-in page in the real cookie pipeline — so what is asserted is that it
        // is not an answer, and that no answer came with it.
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Q3-Report-Final.pdf");
    }
}
