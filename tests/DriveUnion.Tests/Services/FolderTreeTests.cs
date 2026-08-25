using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Tests.Trash;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The customer's folder tree: the rules that decide what a move is allowed to do.
///
/// <para>Most of this file is refusals, and that is the shape of the feature. Creating a folder is
/// one insert; what makes a tree a tree is that some of the things a reader can ask for would leave
/// it not being one — a folder inside itself, a folder inside its own child, a name that already
/// belongs to a sibling — and each of those has to be a sentence rather than a stack trace.</para>
/// </summary>
public class FolderTreeTests
{
    [Fact]
    public async Task A_folder_is_made_where_the_reader_is_standing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        var reports = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        reports.Succeeded.Should().BeTrue();

        var q3 = await harness.Tree().CreateAsync(tenant.Id, user, reports.FolderId, "Q3", default);
        q3.Succeeded.Should().BeTrue();

        (await harness.Tree().ChildrenAsync(tenant.Id, null, default))
            .Should().ContainSingle().Which.Name.Should().Be("Reports");

        (await harness.Tree().ChildrenAsync(tenant.Id, reports.FolderId, default))
            .Should().ContainSingle().Which.Name.Should().Be("Q3");
    }

    [Fact]
    public async Task A_name_is_trimmed_and_a_blank_one_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        (await harness.Tree().CreateAsync(tenant.Id, user, null, "   ", default))
            .Outcome.Should().Be(FolderOutcome.NameEmpty);

        var made = await harness.Tree().CreateAsync(tenant.Id, user, null, "  Reports  ", default);

        // Trimmed rather than refused for having spaces round it, because a pasted name has them
        // more often than not.
        (await harness.Tree().ChildrenAsync(tenant.Id, null, default))
            .Should().ContainSingle().Which.Name.Should().Be("Reports");

        made.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Two_folders_in_one_place_cannot_share_a_name_whatever_the_case()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);

        // «Reports» and «reports» side by side is two folders a reader will put things in at random.
        (await harness.Tree().CreateAsync(tenant.Id, user, null, "reports", default))
            .Outcome.Should().Be(FolderOutcome.NameTaken);

        // …but the same name in a different place is a different folder, and the commonest one there
        // is: every project with a «docs» in it.
        var other = await harness.Tree().CreateAsync(tenant.Id, user, null, "Archive", default);
        (await harness.Tree().CreateAsync(tenant.Id, user, other.FolderId, "Reports", default))
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself_or_into_its_own_child()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        var top = await harness.Tree().CreateAsync(tenant.Id, user, null, "Top", default);
        var middle = await harness.Tree().CreateAsync(tenant.Id, user, top.FolderId, "Middle", default);
        var bottom = await harness.Tree().CreateAsync(tenant.Id, user, middle.FolderId, "Bottom", default);

        // The whole reason this check exists: a subtree detached from the root still has rows, and
        // every walk over it — the breadcrumb, the picker, the depth check — runs for ever.
        (await harness.Tree().MoveAsync(tenant.Id, top.FolderId!.Value, top.FolderId, default))
            .Outcome.Should().Be(FolderOutcome.WouldLoop);

        (await harness.Tree().MoveAsync(tenant.Id, top.FolderId!.Value, middle.FolderId, default))
            .Outcome.Should().Be(FolderOutcome.WouldLoop);

        (await harness.Tree().MoveAsync(tenant.Id, top.FolderId!.Value, bottom.FolderId, default))
            .Outcome.Should().Be(FolderOutcome.WouldLoop);
    }

    [Fact]
    public async Task A_move_that_would_nest_past_the_limit_is_refused_with_the_subtree_counted()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        // A chain as deep as the tree is allowed to go.
        Guid? deepest = null;
        for (var i = 0; i < Folder.MaxDepth; i++)
        {
            var made = await harness.Tree().CreateAsync(tenant.Id, user, deepest, $"level-{i}", default);
            made.Succeeded.Should().BeTrue($"level {i} is within {Folder.MaxDepth}");
            deepest = made.FolderId;
        }

        (await harness.Tree().CreateAsync(tenant.Id, user, deepest, "one-too-many", default))
            .Outcome.Should().Be(FolderOutcome.TooDeep);

        // …and a move counts what is under the folder, not only where it lands. A two-level subtree
        // moved one level below the ceiling would put its own leaves past it, which is the version
        // of this check that a depth-of-the-destination test does not catch.
        var branchTop = await harness.Tree().CreateAsync(tenant.Id, user, null, "branch", default);
        var branchLeaf = await harness.Tree().CreateAsync(tenant.Id, user, branchTop.FolderId, "leaf", default);
        branchLeaf.Succeeded.Should().BeTrue();

        (await harness.Tree().MoveAsync(tenant.Id, branchTop.FolderId!.Value, deepest, default))
            .Outcome.Should().Be(FolderOutcome.TooDeep);
    }

    [Fact]
    public async Task The_breadcrumb_runs_from_the_root_to_the_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = Guid.NewGuid();

        var a = await harness.Tree().CreateAsync(tenant.Id, user, null, "A", default);
        var b = await harness.Tree().CreateAsync(tenant.Id, user, a.FolderId, "B", default);
        var c = await harness.Tree().CreateAsync(tenant.Id, user, b.FolderId, "C", default);

        var path = await harness.Tree().PathAsync(tenant.Id, c.FolderId!.Value, default);

        path.Select(p => p.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task A_folder_with_anything_live_in_it_is_refused_with_the_count()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var file = harness.SeedFile(tenant.Id, account.Id, "q3.pdf");
        await harness.Tree().MoveFileAsync(tenant.Id, file.Id, folder.FolderId, default);

        var refused = await harness.Tree().DeleteAsync(tenant.Id, folder.FolderId!.Value, default);

        refused.Outcome.Should().Be(FolderOutcome.NotEmpty);

        // The count is the whole point of the refusal: «not empty» sends somebody to go and look,
        // and a number tells them what they will find when they get there.
        refused.Contains.Should().Be(1);
    }

    [Fact]
    public async Task A_folder_holding_only_deleted_files_is_empty_and_goes_for_good()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var file = harness.SeedFile(tenant.Id, account.Id, "q3.pdf", deletedAt: ServiceTestHarness.Now);
        await harness.Tree().MoveFileAsync(tenant.Id, file.Id, folder.FolderId, default);

        // A folder whose contents are all in the trash reads as empty because it is — the customer
        // put those somewhere else. What it leaves behind is a trashed row naming a folder that is
        // gone, which TrashService.RestoreAsync answers by landing the file at the root.
        (await harness.Tree().DeleteAsync(tenant.Id, folder.FolderId!.Value, default))
            .Succeeded.Should().BeTrue();

        (await harness.Tree().ChildrenAsync(tenant.Id, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task One_workspace_cannot_see_reach_or_move_anothers_folders()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var user = Guid.NewGuid();

        var yours = await harness.Tree().CreateAsync(theirs.Id, user, null, "Payroll", default);
        var id = yours.FolderId!.Value;

        // The line this product is not allowed to cross, restated for the tree. Every one of these
        // answers «not found» rather than «forbidden», deliberately: telling them apart would tell a
        // caller that somebody else's folder id exists.
        (await harness.Tree().ChildrenAsync(mine.Id, null, default)).Should().BeEmpty();
        (await harness.Tree().PathAsync(mine.Id, id, default)).Should().BeEmpty();
        (await harness.Tree().ExistsAsync(mine.Id, id, default)).Should().BeFalse();

        (await harness.Tree().RenameAsync(mine.Id, id, "Mine now", default))
            .Outcome.Should().Be(FolderOutcome.NotFound);

        (await harness.Tree().MoveAsync(mine.Id, id, null, default))
            .Outcome.Should().Be(FolderOutcome.NotFound);

        (await harness.Tree().DeleteAsync(mine.Id, id, default))
            .Outcome.Should().Be(FolderOutcome.NotFound);

        (await harness.Tree().ChildrenAsync(theirs.Id, null, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Filing_a_file_is_one_column_and_no_drive_call()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var file = harness.SeedFile(tenant.Id, account.Id, "q3.pdf");

        (await harness.Tree().MoveFileAsync(tenant.Id, file.Id, folder.FolderId, default))
            .Succeeded.Should().BeTrue();

        // Browsing is one folder deep, so the root no longer holds it and the folder does.
        (await harness.Files().ListAsync(tenant.Id, new FileListFilter(NameQuery: null), default)).Should().BeEmpty();
        (await harness.Files().ListAsync(tenant.Id, new FileListFilter(FolderId: folder.FolderId), default)).Should().ContainSingle();

        // …and back out again.
        (await harness.Tree().MoveFileAsync(tenant.Id, file.Id, null, default)).Succeeded.Should().BeTrue();
        (await harness.Files().ListAsync(tenant.Id, new FileListFilter(NameQuery: null), default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_search_crosses_folders_and_says_which_one_each_hit_is_in()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);
        var filed = harness.SeedFile(tenant.Id, account.Id, "q3-report.pdf");
        harness.SeedFile(tenant.Id, account.Id, "q4-report.pdf");
        await harness.Tree().MoveFileAsync(tenant.Id, filed.Id, folder.FolderId, default);

        // Searching inside the folder somebody happens to be standing in answers «not found» for a
        // file they own and can see the name of — which is the defect the search box already had
        // once. A search is the whole workspace, and the row says where the hit lives.
        var hits = await harness.Files().ListAsync(tenant.Id, new FileListFilter(NameQuery: "report"), default);

        hits.Should().HaveCount(2);
        hits.Should().ContainSingle(f => f.FolderId == folder.FolderId);
        hits.Should().ContainSingle(f => f.FolderId == null);
    }

    [Fact]
    public async Task A_file_restored_into_a_folder_that_is_gone_comes_back_to_the_root()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folder = await harness.Tree().CreateAsync(tenant.Id, user, null, "Reports", default);

        // Seeded into the fake Drive as well as into the catalogue, because the delete below is the
        // real one and the trash mover asks Drive to move a file it has to have heard of.
        var file = await harness.SeedUploadedFileAsync(tenant, account, name: "q3.pdf");
        await harness.Tree().MoveFileAsync(tenant.Id, file.Id, folder.FolderId, default);

        // The real delete path, with the trash mover behind it, so the row that comes back is one
        // that actually went through Drive rather than a soft delete standing in for it.
        await harness.FilesInTrash().DeleteAsync(tenant.Id, file.Id, default);
        (await harness.Tree().DeleteAsync(tenant.Id, folder.FolderId!.Value, default)).Succeeded.Should().BeTrue();

        (await harness.Trash().RestoreAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        // Somewhere the customer can see, rather than into a folder that is not there. This is the
        // case StoredFile.FolderId has no foreign key for: a cascade would have taken the file with
        // the folder, and a restrict would have refused the delete for a reason nobody could see.
        var listed = await harness.Files().ListAsync(tenant.Id, new FileListFilter(NameQuery: null), default);

        listed.Should().ContainSingle().Which.FolderId.Should().BeNull();
    }
}
