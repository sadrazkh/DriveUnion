using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Labels: the axis that cuts across the folder tree rather than nesting inside it.
///
/// <para>A file is in one folder and has as many labels as it has aspects. The tree can express one
/// of those by nesting and would then have to pick which one — «by client», «by year», «by whether
/// it is paid» — which is the choice this exists to avoid.</para>
/// </summary>
public class TagStoreTests
{
    [Fact]
    public async Task Typing_the_same_label_twice_lands_on_one_of_them()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var first = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);
        var again = await harness.Labels().EnsureAsync(tenant.Id, "urgent", default);
        var spaced = await harness.Labels().EnsureAsync(tenant.Id, "  URGENT  ", default);

        // The screen's only way to make a label is to type it onto a file. Two people typing the
        // same word must land on one label, or the filter is split in half without either of them
        // being told — and «Urgent» beside «urgent» in the list is the visible half of that.
        again.TagId.Should().Be(first.TagId);
        spaced.TagId.Should().Be(first.TagId);

        (await harness.Labels().ListAsync(tenant.Id, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_blank_label_is_refused_and_the_ceiling_is_a_sentence()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        (await harness.Labels().EnsureAsync(tenant.Id, "   ", default))
            .Outcome.Should().Be(TagOutcome.NameEmpty);

        for (var i = 0; i < Tag.MaxPerTenant; i++)
        {
            (await harness.Labels().EnsureAsync(tenant.Id, $"label-{i}", default)).Succeeded.Should().BeTrue();
        }

        // Not a storage limit — the rows are tiny. The list is drawn in full on the files screen so
        // somebody can filter by pressing one, and past this it is a wall rather than a list.
        (await harness.Labels().EnsureAsync(tenant.Id, "one too many", default))
            .Outcome.Should().Be(TagOutcome.TooMany);
    }

    [Fact]
    public async Task Applying_a_label_twice_changes_nothing_the_second_time()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var one = harness.SeedFile(tenant.Id, account.Id, "one.pdf");
        var two = harness.SeedFile(tenant.Id, account.Id, "two.pdf");
        var tag = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);

        var first = await harness.Labels().ApplyAsync(tenant.Id, [one.Id, two.Id], tag.TagId!.Value, default);
        first.Affected.Should().Be(2);

        // The count is what changed and never what was asked for. A second press of «برچسب بزن» on
        // the same selection is a no-op, and saying «۲ فایل» again would be a report of work that
        // did not happen.
        var second = await harness.Labels().ApplyAsync(tenant.Id, [one.Id, two.Id], tag.TagId!.Value, default);
        second.Affected.Should().Be(0);
    }

    [Fact]
    public async Task A_label_filters_the_list_across_every_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var filed = harness.SeedFile(tenant.Id, account.Id, "q3.pdf");
        var loose = harness.SeedFile(tenant.Id, account.Id, "note.txt");
        harness.SeedFile(tenant.Id, account.Id, "unlabelled.txt");

        await harness.Tree().MoveFileAsync(tenant.Id, filed.Id, folder.FolderId, default);

        var tag = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);
        await harness.Labels().ApplyAsync(tenant.Id, [filed.Id, loose.Id], tag.TagId!.Value, default);

        // The whole point: a label reaches the file in the folder and the file at the root in one
        // answer. If it respected the folder it would be a worse folder.
        var found = await harness.Files().ListAsync(tenant.Id, new FileListFilter(TagId: tag.TagId), default);

        found.Select(f => f.Name).Should().BeEquivalentTo(["q3.pdf", "note.txt"]);
    }

    [Fact]
    public async Task A_label_and_a_search_narrow_each_other()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var wanted = harness.SeedFile(tenant.Id, account.Id, "q3-report.pdf");
        var alsoLabelled = harness.SeedFile(tenant.Id, account.Id, "note.txt");
        harness.SeedFile(tenant.Id, account.Id, "q4-report.pdf");

        var tag = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);
        await harness.Labels().ApplyAsync(tenant.Id, [wanted.Id, alsoLabelled.Id], tag.TagId!.Value, default);

        // Both filters, not one replacing the other: «the urgent ones called report» is the question
        // somebody with a hundred files actually has.
        var found = await harness.Files().ListAsync(
            tenant.Id,
            new FileListFilter(NameQuery: "report", TagId: tag.TagId),
            default);

        found.Should().ContainSingle().Which.Name.Should().Be("q3-report.pdf");
    }

    [Fact]
    public async Task Retiring_a_label_leaves_the_files_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var file = harness.SeedFile(tenant.Id, account.Id, "one.pdf");
        var tag = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);
        await harness.Labels().ApplyAsync(tenant.Id, [file.Id], tag.TagId!.Value, default);

        var retired = await harness.Labels().DeleteAsync(tenant.Id, tag.TagId!.Value, default);

        retired.Succeeded.Should().BeTrue();
        retired.Affected.Should().Be(1, "the sentence on the screen says how many files it came off");

        (await harness.Labels().ListAsync(tenant.Id, default)).Should().BeEmpty();

        // The file itself is untouched, which is the sentence the screen makes and the property
        // behind it.
        (await harness.Files().ListAsync(tenant.Id, new FileListFilter(), default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_labels_count_is_the_live_files_carrying_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var live = harness.SeedFile(tenant.Id, account.Id, "live.pdf");
        var binned = harness.SeedFile(tenant.Id, account.Id, "binned.pdf", deletedAt: ServiceTestHarness.Now);
        var tag = await harness.Labels().EnsureAsync(tenant.Id, "Urgent", default);

        await harness.Labels().ApplyAsync(tenant.Id, [live.Id], tag.TagId!.Value, default);

        // Applying to a deleted file does nothing at all: the selection is filtered to this
        // workspace's live rows before anything is written.
        (await harness.Labels().ApplyAsync(tenant.Id, [binned.Id], tag.TagId!.Value, default))
            .Affected.Should().Be(0);

        // …and the count promises what pressing the label will show. A number that included the
        // trash would be one the reader cannot see the parts of.
        (await harness.Labels().ListAsync(tenant.Id, default))
            .Should().ContainSingle().Which.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task One_workspace_cannot_label_or_read_anothers_files()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();

        var yours = harness.SeedFile(theirs.Id, account.Id, "payroll.pdf");
        var myTag = await harness.Labels().EnsureAsync(mine.Id, "Urgent", default);

        // The line this product is not allowed to cross, restated for labels. Both halves: my tag
        // cannot reach their file, and their tag id is not a tag as far as my workspace is
        // concerned.
        (await harness.Labels().ApplyAsync(mine.Id, [yours.Id], myTag.TagId!.Value, default))
            .Affected.Should().Be(0);

        var theirTag = await harness.Labels().EnsureAsync(theirs.Id, "Payroll", default);

        (await harness.Labels().ApplyAsync(mine.Id, [yours.Id], theirTag.TagId!.Value, default))
            .Outcome.Should().Be(TagOutcome.NotFound);

        (await harness.Labels().ListAsync(mine.Id, default)).Should().ContainSingle().Which.Name.Should().Be("Urgent");
    }

    [Fact]
    public async Task A_selection_moves_in_one_statement_and_counts_what_moved()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(mine.Id, user, null, "Reports", default);
        var one = harness.SeedFile(mine.Id, account.Id, "one.pdf");
        var two = harness.SeedFile(mine.Id, account.Id, "two.pdf");
        var binned = harness.SeedFile(mine.Id, account.Id, "gone.pdf", deletedAt: ServiceTestHarness.Now);
        var yours = harness.SeedFile(theirs.Id, account.Id, "payroll.pdf");

        var moved = await harness.Tree().MoveFilesAsync(
            mine.Id,
            [one.Id, two.Id, binned.Id, yours.Id],
            folder.FolderId,
            default);

        // Two of the four. The other workspace's file and the deleted one are not refused — they are
        // never matched, which is the same predicate every UPDATE in this layer carries.
        moved.Contains.Should().Be(2);

        (await harness.Files().ListAsync(mine.Id, new FileListFilter(FolderId: folder.FolderId), default))
            .Select(f => f.Name).Should().BeEquivalentTo(["one.pdf", "two.pdf"]);
    }
}
