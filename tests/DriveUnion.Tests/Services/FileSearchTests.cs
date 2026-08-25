using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The shell's search box, from the catalogue's side.
///
/// <para>The box was in the header from the first screen and submitted <c>q</c> to <c>/files</c>
/// the whole time. <c>FilesController.Index</c> took <c>selected</c> and a cancellation token and
/// nothing else, so the parameter was bound to nothing and every search answered with the whole
/// list. Nothing failed: the form posted, the page returned 200, the results looked like results.
/// A reader searching for a file they own and being shown everything else they own concludes the
/// file is gone.</para>
///
/// <para>These run over SQLite, which is what makes the case-folding test worth having and worth
/// reading carefully — see the comment on the provider difference in <c>FileCatalog.ListAsync</c>.</para>
/// </summary>
public class FileSearchTests
{
    [Fact]
    public async Task A_term_narrows_the_list_to_the_names_that_contain_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "Q3-report.pdf");
        harness.SeedFile(tenant.Id, account.Id, "Q4-report.pdf");
        harness.SeedFile(tenant.Id, account.Id, "holiday-photo.jpg");

        var found = await harness.Files().ListAsync(tenant.Id, folderId: null, "report", default);

        found.Select(f => f.Name).Should().BeEquivalentTo(["Q3-report.pdf", "Q4-report.pdf"]);
    }

    [Fact]
    public async Task Part_of_a_name_is_enough_and_so_is_the_extension()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "quarterly-earnings.xlsx");
        harness.SeedFile(tenant.Id, account.Id, "holiday-photo.jpg");

        // A substring anywhere, because that is how somebody looks for a file whose exact name they
        // do not remember — which is the only reason to open a search box at all.
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "earn", default))
            .Should().ContainSingle().Which.Name.Should().Be("quarterly-earnings.xlsx");

        (await harness.Files().ListAsync(tenant.Id, folderId: null, "xlsx", default))
            .Should().ContainSingle().Which.Name.Should().Be("quarterly-earnings.xlsx");
    }

    [Fact]
    public async Task Case_does_not_decide_whether_a_file_is_found()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "Q3-Report-Final.pdf");

        // The one that would have shipped. `Name.Contains(term)` becomes a LIKE, and LIKE is
        // case-sensitive on Postgres and case-insensitive on SQLite — so the natural spelling passes
        // here and finds nothing for a customer. Both directions asserted, because folding only the
        // column or only the term is a half-fix that also passes one of these.
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "report", default)).Should().ContainSingle();
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "REPORT", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_wildcard_is_a_character_to_search_for_and_not_a_pattern()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "100% done.txt");
        harness.SeedFile(tenant.Id, account.Id, "holiday-photo.jpg");

        // A hand-built LIKE pattern would answer this with both files, and «_» with every file whose
        // name has at least one character. EF's own Contains translation escapes them, which is the
        // reason the query is written as Contains rather than as a pattern.
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "%", default))
            .Should().ContainSingle().Which.Name.Should().Be("100% done.txt");

        (await harness.Files().ListAsync(tenant.Id, folderId: null, "_", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_or_blank_term_is_the_whole_list_rather_than_a_search_for_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "one.txt");
        harness.SeedFile(tenant.Id, account.Id, "two.txt");

        // «?q=» is what an empty box submits, and a box holding spaces is what a stray keystroke
        // leaves. Neither is a term, and answering either with an empty table would tell a reader
        // their workspace is empty.
        (await harness.Files().ListAsync(tenant.Id, folderId: null, null, default)).Should().HaveCount(2);
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "", default)).Should().HaveCount(2);
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "   ", default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_term_is_trimmed_before_it_is_matched()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        harness.SeedFile(tenant.Id, account.Id, "report.pdf");

        // Pasting a file name picks up a trailing space more often than not, and a search that
        // answers «nothing matched» to a name that is right except for whitespace is a search the
        // reader stops trusting.
        (await harness.Files().ListAsync(tenant.Id, folderId: null, "  report  ", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_search_reaches_neither_the_trash_nor_another_workspace()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        harness.SeedFile(mine.Id, account.Id, "shared-name.pdf");
        harness.SeedFile(theirs.Id, account.Id, "shared-name.pdf");
        harness.SeedFile(mine.Id, account.Id, "shared-name-deleted.pdf", deletedAt: ServiceTestHarness.Now);

        // The two predicates the filter is added to, restated as a property of searching rather than
        // of listing: a WHERE bolted onto a query is exactly where a tenant scope goes missing, and
        // a deleted file surfacing in a search is the trash leaking into the screen it was built to
        // stay out of.
        var found = await harness.Files().ListAsync(mine.Id, folderId: null, "shared-name", default);

        found.Should().ContainSingle().Which.Name.Should().Be("shared-name.pdf");
    }
}
