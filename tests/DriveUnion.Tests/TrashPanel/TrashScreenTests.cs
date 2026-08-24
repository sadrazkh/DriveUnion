using System.Globalization;
using System.Net;
using DriveUnion.Web.Localization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// «سطل زباله», over HTTP, through the pipeline a browser would meet.
///
/// <para>Two things are under test and they are different in kind. One is the isolation: a workspace
/// sees its own trash and can act only on its own rows, which is §8's rule at the one screen that
/// hands a file id back to the server. The other is the shape of the writes — a POST with a token,
/// and no GET that does the same thing — because emptying a trash is the only action in this product
/// that cannot be undone, and a destructive GET is fired by an <c>&lt;img src&gt;</c> on any page and
/// by any browser that prefetches.</para>
/// </summary>
public class TrashScreenTests
{
    [Fact]
    public async Task A_tenant_sees_its_own_trash_and_not_another_workspaces()
    {
        using var harness = new TrashPanelHarness();

        var mine = harness.SeedWorkspace("Acme");
        var theirs = harness.SeedWorkspace("Globex");

        harness.SeedTrashedFile(mine, "acme-invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));
        harness.SeedTrashedFile(theirs, "globex-secret.pdf", 8192, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(mine.Id);
        var html = await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        html.Should().Contain("acme-invoice.pdf");
        html.Should().NotContain(
            "globex-secret.pdf",
            "the listing is scoped by the claim's tenant, and a second workspace's file name on this "
            + "page is the whole of the isolation failure §8 exists to prevent");
    }

    [Fact]
    public async Task A_live_file_is_not_in_the_trash()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        harness.SeedLiveFile(tenant, "still-here.pdf", 4096);
        harness.SeedTrashedFile(tenant, "gone-for-now.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        html.Should().Contain("gone-for-now.pdf");
        html.Should().NotContain("still-here.pdf");
    }

    /// <summary>
    /// The sentence this whole phase exists to say. A customer who deletes a file, watches their
    /// usage figure stay where it was and is told nothing concludes the product is broken — which is
    /// the report that started P1.
    /// </summary>
    [Fact]
    public async Task The_screen_says_plainly_that_only_emptying_frees_space()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)));

        main.Should().Contain(UiText.Trash.HowItWorksHeading);
        main.Should().Contain(UiText.Trash.HowItWorksBody);

        // …and the button the sentence points at is on the same screen.
        main.Should().Contain(UiText.Trash.Empty);
    }

    [Fact]
    public async Task The_screen_says_when_each_file_will_be_purged()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");

        // An hour past nine whole days, because the screen floors what is left: seeded at exactly
        // nine days, the render is already a few milliseconds later and the cell reads eight.
        harness.SeedTrashedFile(tenant, "with-deadline.pdf", 4096, DateTimeOffset.UtcNow.AddDays(9).AddHours(1));

        // Deleted before the trash existed: no deadline, and the sweeper leaves it alone rather than
        // inventing one. The cell has to say so — a blank reads as «never».
        harness.SeedTrashedFile(tenant, "no-deadline.pdf", 4096, purgeAfter: null);

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)));

        main.Should().Contain(UiText.Trash.PurgeInDays(9));
        main.Should().Contain(UiText.Trash.PurgeNoDeadline);
    }

    [Fact]
    public async Task The_screen_says_how_much_the_trash_is_holding()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        harness.SeedTrashedFile(tenant, "a.pdf", 3L * 1024 * 1024, DateTimeOffset.UtcNow.AddDays(30));
        harness.SeedTrashedFile(tenant, "b.pdf", 5L * 1024 * 1024, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)));

        main.Should().Contain(UiText.Trash.HoldingSize("8 MB"));
    }

    // ------------------------------------------------------------------ the shape of the writes

    [Theory]
    [InlineData("/trash/empty")]
    public async Task A_get_cannot_empty_the_trash(string path)
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "a destructive GET is fired by any «img src» on any page and by a browser that prefetches");

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.CountAsync(f => f.TenantId == tenant.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_get_cannot_restore_a_file()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        var file = harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri($"/trash/{file.Id}/restore", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.SingleAsync(f => f.Id == file.Id)).DeletedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("/trash/empty")]
    [InlineData("/trash/restore")]
    public async Task A_post_without_the_token_is_refused(string path)
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        var file = harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        var address = path == "/trash/restore" ? $"/trash/{file.Id}/restore" : path;

        using var client = harness.NewClient(tenant.Id, keepCookies: true);

        // The cookie half of the pair is issued by rendering the page; the field half is left out on
        // purpose, which is what a cross-site form can and cannot produce.
        await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        using var response = await client.PostAsync(
            new Uri(address, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.SingleAsync(f => f.Id == file.Id)).DeletedAt.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ what the buttons do

    [Fact]
    public async Task Restoring_puts_the_file_back_and_says_so()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        var file = harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");

        using var response = await TrashPanelHarness.PostAsync(client, $"/trash/{file.Id}/restore", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();
        var row = await db.StoredFiles.SingleAsync(f => f.Id == file.Id);

        row.DeletedAt.Should().BeNull();
        row.PurgeAfter.Should().BeNull();

        PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)))
            .Should().Contain(UiText.Trash.Restored);
    }

    [Fact]
    public async Task A_tenant_cannot_restore_another_workspaces_file()
    {
        using var harness = new TrashPanelHarness();

        var mine = harness.SeedWorkspace("Acme");
        var theirs = harness.SeedWorkspace("Globex");
        var theirFile = harness.SeedTrashedFile(theirs, "globex-secret.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(mine.Id, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");

        using var response = await TrashPanelHarness.PostAsync(client, $"/trash/{theirFile.Id}/restore", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.SingleAsync(f => f.Id == theirFile.Id)).DeletedAt.Should().NotBeNull(
            "a file id from another workspace has to be the same answer as one that was already purged");

        // …and the sentence it is answered with is the one that says nothing about whose it was.
        PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)))
            .Should().Contain(UiText.Trash.NotRestored);
    }

    [Fact]
    public async Task Emptying_destroys_the_files_and_frees_the_space()
    {
        using var harness = new TrashPanelHarness();

        const long held = 6L * 1024 * 1024;

        // The workspace is carrying those bytes: deleting stamped a column and released nothing,
        // which is exactly the bug this screen exists to make visible.
        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: held);
        harness.SeedTrashedFile(tenant, "invoice.pdf", held, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");

        using var response = await TrashPanelHarness.PostAsync(client, "/trash/empty", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();

        (await db.StoredFiles.CountAsync(f => f.TenantId == tenant.Id)).Should().Be(0);
        (await db.Tenants.SingleAsync(t => t.Id == tenant.Id)).StorageUsedBytes.Should().Be(
            0,
            "emptying is the only thing in this product that gives a customer their space back");

        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)));
        main.Should().Contain(UiText.Trash.Emptied(1));
        main.Should().Contain(UiText.Trash.EmptyStateHeading);
    }

    [Fact]
    public async Task Emptying_one_workspace_leaves_another_workspaces_trash_alone()
    {
        using var harness = new TrashPanelHarness();

        var mine = harness.SeedWorkspace("Acme");
        var theirs = harness.SeedWorkspace("Globex");

        harness.SeedTrashedFile(mine, "acme-invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));
        var theirFile = harness.SeedTrashedFile(theirs, "globex-secret.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(mine.Id, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");

        using var response = await TrashPanelHarness.PostAsync(client, "/trash/empty", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.CountAsync(f => f.Id == theirFile.Id)).Should().Be(1);
    }

    [Fact]
    public async Task An_empty_trash_offers_nothing_to_empty()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/trash", UriKind.Relative)));

        main.Should().Contain(UiText.Trash.EmptyStateHeading);

        // The address rather than the label: the sentence above the list explains what emptying does
        // and names it, so the words are on the page whether or not there is anything to press.
        main.Should().NotContain(
            "/trash/empty",
            "a button whose only outcome is «there was nothing to delete» teaches a customer that "
            + "the control does nothing");
    }

    [Fact]
    public async Task A_caller_with_no_workspace_does_not_reach_the_trash()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        using var response = await client.GetAsync(new Uri("/trash", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the trash is a workspace's own screen, and an operator has no workspace to hold one");
    }

    /// <summary>
    /// The table declares exactly as many tracks as it draws columns — the rule
    /// <c>PanelLayoutTests</c> holds for the screens that were built before this one. A track too few
    /// and the last column is drawn over the one before it; a track too many and every row ends in a
    /// gap the header does not have.
    /// </summary>
    [Fact]
    public async Task The_table_declares_as_many_tracks_as_it_draws_columns()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme");
        harness.SeedTrashedFile(tenant, "invoice.pdf", 4096, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        var (cols, headCells) = PanelMarkup.SingleTable(html);

        PanelMarkup.TrackCount(cols).Should().Be(headCells, "«{0}» is the trash table's tracks", cols);

        // …and the flexible track has a floor, because minmax(0, Nfr) answers "there is no room" by
        // drawing the file name 0px wide, which is the one column that tells two rows apart.
        cols.Should().Contain("minmax(var(--name-min)");
    }

    /// <summary>
    /// Every Latin readout on the screen carries its own direction.
    ///
    /// <para>«4.7 GB» in an RTL box is a European number, a neutral space and a Latin run: the bidi
    /// algorithm resolves the space to the paragraph's direction and lays the two out as «GB 4.7».
    /// The same guard <c>PanelLayoutTests</c> runs over the older screens, run here because this one
    /// is not on its list and cannot be added to it from this slice.</para>
    /// </summary>
    [Fact]
    public async Task A_latin_readout_carries_its_own_direction()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: 9L * 1024 * 1024 * 1024);
        harness.SeedTrashedFile(tenant, "invoice.pdf", 18L * 1024 * 1024, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        var readouts = PanelMarkup.LatinReadouts(html);

        readouts.Should().NotBeEmpty("the page draws a file's size and the sidebar draws three figures");

        readouts.Where(leaf => !leaf.Attributes.Contains(@"dir=""ltr""", StringComparison.Ordinal))
            .Select(leaf => leaf.Text.Trim())
            .Should().BeEmpty("each of these is laid out with its unit before its number without it");
    }

    /// <summary>The percentage the sidebar's bar is drawn with is written invariantly.</summary>
    [Fact]
    public async Task A_bar_width_is_a_number_a_browser_can_read()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: 25L * 1024 * 1024 * 1024);

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri("/trash", UriKind.Relative));

        // 25 GB of a 100 GB cap. A culture that punctuates decimals with a comma would write «25,5%»
        // into a style attribute, which a browser drops silently.
        html.Should().Contain(
            string.Create(CultureInfo.InvariantCulture, $"width: {25}%"),
            "the width is formatted with the invariant culture, as every figure in this panel is");
    }
}
