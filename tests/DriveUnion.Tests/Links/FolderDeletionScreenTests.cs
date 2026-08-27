using System.Net;
using DriveUnion.Core.Storage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Links;

/// <summary>
/// The two things «فایل‌ها» has to show now that a delete can be bigger than a request.
///
/// <para>One is the verb: a folder with things in it offers «حذف پوشه و هرچه داخلش است», and an
/// empty one offers the plain delete that only ever destroys a name. Drawing both would put the
/// button that refuses next to the button that works, under the same word.</para>
///
/// <para>The other is that a clean-up still running is said out loud. The files are already out of
/// the table and already in the trash, so nothing on this screen would otherwise hint that the trash
/// is still filling up half a minute after the press.</para>
/// </summary>
public class FolderDeletionScreenTests
{
    [Fact]
    public async Task A_folder_with_something_in_it_offers_the_verb_that_takes_the_contents()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        var folder = FileEverythingInto(harness, tenant.Id, "Reports");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync($"/files?folder={folder}");

        markup.Should().MatchRegex(
            $@"action=""/files/folders/{folder}/delete-everything""",
            "a folder holding files has to offer the delete that can actually take them");

        // …and not the one that would refuse. Two danger buttons a click apart, both reading «حذف»,
        // is a screen where the reader finds out which is which by pressing one.
        markup.Should().NotContain($"/files/folders/{folder}/delete\"");

        var text = WebUtility.HtmlDecode(markup);
        text.Should().Contain("حذف پوشه و هرچه داخلش است");
    }

    [Fact]
    public async Task An_empty_folder_still_offers_the_plain_delete()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");
        var folder = MakeFolder(harness, tenant.Id, "Empty");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync($"/files?folder={folder}");

        // Nothing in it, so there is nothing to warn about: this one destroys a name and refuses
        // anything else. A label promising to take «everything in it» over an empty folder would be
        // ceremony in front of nothing.
        markup.Should().MatchRegex($@"action=""/files/folders/{folder}/delete""");
        markup.Should().NotContain("delete-everything");
    }

    [Fact]
    public async Task A_clean_up_still_running_is_said_on_the_screen_it_was_pressed_on()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using (var db = harness.NewDbContext())
        {
            db.DeletionJobs.Add(new DeletionJob
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                Scope = DeletionScope.Folder,
                FolderName = "Reports",
                FilesTotal = 5000,
                FilesMoved = 120,
                Status = DeletionJobStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            db.SaveChanges();
        }

        using var client = harness.NewClient(tenant.Id);

        // Decoded first, the way every panel test reads a page: this app configures no web encoder,
        // so Razor writes every non-ASCII character as a numeric entity.
        var text = WebUtility.HtmlDecode(await client.GetStringAsync("/files"));

        text.Should().Contain("Reports", "the line names what was deleted rather than only counting it");
        text.Should().Contain("لازم نیست منتظر بمانید");
    }

    [Fact]
    public async Task A_workspace_with_nothing_running_is_told_nothing()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var text = WebUtility.HtmlDecode(await client.GetStringAsync("/files"));

        // The normal state, and it has to be silent: a status line that is always there is one
        // nobody reads on the day it says something.
        text.Should().NotContain("لازم نیست منتظر بمانید");
    }

    private static Guid MakeFolder(PanelPageHarness harness, Guid tenantId, string name)
    {
        using var db = harness.NewDbContext();

        var folder = new Folder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Folders.Add(folder);
        db.SaveChanges();

        return folder.Id;
    }

    /// <summary>A folder with the workspace's file in it, which is what makes the verb change.</summary>
    private static Guid FileEverythingInto(PanelPageHarness harness, Guid tenantId, string name)
    {
        var folderId = MakeFolder(harness, tenantId, name);

        using var db = harness.NewDbContext();

        db.StoredFiles
            .Where(f => f.TenantId == tenantId)
            .ExecuteUpdate(s => s.SetProperty(f => f.FolderId, folderId));

        return folderId;
    }
}
