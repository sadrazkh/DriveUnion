using System.Net;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Tests.TrashPanel;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.Dashboard;

/// <summary>
/// <c>/</c>, rendered.
///
/// <para>Two screens behind one route, and the half of this file that matters most is the half that
/// asserts an <b>absence</b>. A leak here is silent: the customer's dashboard renders, every figure
/// on it looks plausible, and the only thing that has happened is that a paying customer now knows
/// the operator's Google address and how much of a shared pool is left. Nothing fails, no test goes
/// red, and the product model M1 §2 is built on is gone.</para>
///
/// <para>Assertions read decoded markup. Razor's encoder writes everything outside Basic Latin as
/// <c>&amp;#x641;…</c>, so <c>NotContain("سهمیه")</c> against raw markup passes on a page that
/// leaked the word — which is the exact failure this file exists to catch.</para>
/// </summary>
public class DashboardScreenTests
{
    private const long TwentyGigabytes = 20L * 1024 * 1024 * 1024;

    private const long EighteenMegabytes = 18L * 1024 * 1024;

    private const string PoolAddress = "archive.main@gmail.com";

    /// <summary>
    /// The complaint this slice answers: «داشبورد هم خالیه». It was not empty, there was no
    /// dashboard — <c>/</c> answered with a redirect to another screen.
    /// </summary>
    [Fact]
    public async Task The_root_renders_a_dashboard_instead_of_redirecting()
    {
        using var harness = new DashboardHarness();
        var tenant = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the sidebar's first item says «داشبورد» and points here; it has to open one");

        var main = PanelMarkup.MainContent(await response.Content.ReadAsStringAsync());

        main.Should().Contain(UiText.Shell.Dashboard);
        main.Should().Contain(UiText.Dashboard.CustomerSubtitle);
    }

    [Fact]
    public async Task A_customers_dashboard_renders_their_own_figures()
    {
        using var harness = new DashboardHarness();

        var account = harness.SeedAccount("A1", PoolAddress);
        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: TwentyGigabytes);

        var file = harness.SeedFile(tenant, account.Id, "Q3-Report-Final.pdf", EighteenMegabytes);
        harness.SeedFile(tenant, account.Id, "gone.pdf", EighteenMegabytes, deletedAt: DateTimeOffset.UtcNow);

        harness.SeedLink(tenant, file.Id, "kx91mzq4", downloadCount: 3, maxDownloads: 500);

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(
            UiText.Plans.OfCap(
                DisplayFormats.Bytes(TwentyGigabytes),
                DisplayFormats.Bytes(DashboardHarness.StorageQuotaBytes)),
            "storage spent against this workspace's own cap is the first question the screen answers");

        main.Should().Contain(
            UiText.Dashboard.FilesStored(1),
            "the deleted file is in the trash and is not one of the files they have");

        main.Should().Contain(UiText.Dashboard.LiveOfTotal(1, 1));

        // The lifetime download figure, and the sentence that says it is a lifetime figure. There is
        // no «this month» here and the screen must not let the number be read as one.
        main.Should().Contain(UiText.Dashboard.DownloadsAllTimeLabel);
        main.Should().Contain(UiText.Dashboard.DownloadsAreLifetime);

        main.Should().Contain("Q3-Report-Final.pdf", "the newest upload, with a way into it");
        main.Should().Contain("/d/kx91mzq4", "the busiest link's own address");
        main.Should().Contain(UiText.Dashboard.DownloadsOfCap(3, 500));

        // The trash, because it is exactly the difference between what a customer believes they
        // freed and what they actually did.
        main.Should().Contain(UiText.Dashboard.TrashHolds(1));
        main.Should().Contain(DisplayFormats.Bytes(EighteenMegabytes));
    }

    /// <summary>
    /// The traffic figure, which is now counted rather than drawn as a dash.
    ///
    /// <para>This test used to assert the opposite, and the reason it did is worth keeping: a zero
    /// beside an allowance would have told a customer who had been serving downloads all month that
    /// they had used none of what they paid for. The dash was the honest answer while nothing
    /// counted. What makes a zero honest now is that something does — so what is asserted is a
    /// figure that <i>moves</i>, because a screen that always prints zero is the placeholder the
    /// dash was there to refuse, wearing a number.</para>
    /// </summary>
    [Fact]
    public async Task A_customers_dashboard_draws_the_traffic_it_counted()
    {
        using var harness = new DashboardHarness();
        var tenant = harness.SeedWorkspace("Acme");

        const long served = 7L * 1024 * 1024 * 1024;
        harness.SeedTrafficThisMonth(tenant.Id, served);

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(
            UiText.Capacity.TrafficOfCap(
                DisplayFormats.Bytes(served),
                DisplayFormats.Bytes(DashboardHarness.MonthlyEgressBytes)),
            "what ITrafficMeter counted off the response body, against the allowance the plan sells");

        main.Should().Contain(
            UiText.Capacity.TrafficCounts,
            "and the screen says what would make that number go up");

        main.Should().NotContain(
            UiText.Capacity.TrafficOfCap(
                DisplayFormats.Bytes(0),
                DisplayFormats.Bytes(DashboardHarness.MonthlyEgressBytes)),
            "a workspace that has served seven gigabytes must not be shown a zero");
    }

    /// <summary>
    /// …and a workspace that has served nothing is shown a zero rather than the old dash.
    ///
    /// <para>The pair of the test above, and the half that says the number is real in both
    /// directions: a meter that only ever reported what it was seeded with would pass that one.</para>
    /// </summary>
    [Fact]
    public async Task A_workspace_that_has_served_nothing_is_shown_a_zero()
    {
        using var harness = new DashboardHarness();
        var tenant = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(
            UiText.Capacity.TrafficOfCap(
                DisplayFormats.Bytes(0),
                DisplayFormats.Bytes(DashboardHarness.MonthlyEgressBytes)));
    }

    /// <summary>
    /// <b>The leak worth a test.</b> Everything M1 §1.4 says a customer must never see, asserted
    /// absent from the whole document rather than from the page's own region — the shell is part of
    /// what a customer's browser receives, and «not drawn» and «not sent» are different things.
    /// </summary>
    [Fact]
    public async Task A_customers_dashboard_never_carries_the_pool()
    {
        using var harness = new DashboardHarness();

        var account = harness.SeedAccount("A1", PoolAddress, usedBytes: TwentyGigabytes);
        harness.SeedAccount("A2", "archive.cold@gmail.com", GoogleAccountStatus.Disconnected);

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: TwentyGigabytes);
        harness.SeedFile(tenant, account.Id, "Q3-Report-Final.pdf", EighteenMegabytes);

        // Another workspace, over its own cap. A customer must not learn it exists, let alone that
        // it is nearly full.
        harness.SeedWorkspace("Globex", storageUsedBytes: TwentyGigabytes, storageQuotaBytes: TwentyGigabytes);

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        // The whole document, not just <main>: the shell is part of what a customer's browser
        // receives, and «not drawn» and «not sent» are different things.
        var page = PanelMarkup.Decode(html);

        page.Should().NotContain(
            UiText.Shell.TodaysUploadQuota,
            "«سهمیه آپلود امروز» is the operator's daily allowance across their Google accounts, and "
            + "a customer must not learn that a pool exists");

        page.Should().NotContain(PoolAddress, "the address of the account holding this customer's file");
        page.Should().NotContain("@gmail.com", "nor any other account's");

        page.Should().NotContain(UiText.Dashboard.PoolHeading);
        page.Should().NotContain(UiText.Dashboard.PoolUsedLabel);
        page.Should().NotContain(UiText.Dashboard.DailyUploadNotMetered);
        page.Should().NotContain(UiText.Dashboard.EgressNotMetered);
        page.Should().NotContain(UiText.Dashboard.WorkspacesHeading);
        page.Should().NotContain("Globex", "another workspace's name is not this customer's business");
        page.Should().NotContain(
            DisplayFormats.Bytes(DashboardHarness.AccountTotalBytes),
            "the pool's size, which the operator's cards print and this one must not");

        // Scoped to the page's own region, unlike the assertions above: a hashed asset URL in the
        // head is three digits at a time and would fail this for reasons that have nothing to do
        // with the pool.
        var main = PanelMarkup.MainContent(html);

        main.Should().NotContain("750", "the daily per-account figure Google allows the operator");

        // The account label is the operator's handle for a Google account. Two characters, so it is
        // asserted where it would actually appear: as the whole text of the square the operator's
        // card draws it in.
        main.Should().NotContain(">A1<");
        main.Should().NotContain(">A2<");
    }

    /// <summary>
    /// The positive control for the assertion above. Without it «the customer's page does not
    /// contain the pool» would pass on a panel where the pool was never rendered for anybody.
    /// </summary>
    [Fact]
    public async Task An_operators_dashboard_renders_the_pool()
    {
        using var harness = new DashboardHarness();

        harness.SeedAccount("A1", PoolAddress, usedBytes: TwentyGigabytes);
        harness.SeedWorkspace("Acme", storageUsedBytes: TwentyGigabytes);

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var main = PanelMarkup.MainContent(html);

        main.Should().Contain(UiText.Dashboard.PoolHeading);
        main.Should().Contain(UiText.Dashboard.OperatorSubtitle);
        main.Should().Contain(PoolAddress, "the operator's own account, on the operator's own screen");
        main.Should().Contain(">A1<", "the operator's handle for that account, in the comp's square");
        main.Should().Contain(UiText.Accounts.StatusHealthy);

        main.Should().Contain(
            UiText.Plans.OfCap(
                DisplayFormats.Bytes(TwentyGigabytes),
                DisplayFormats.Bytes(DashboardHarness.AccountTotalBytes)),
            "what the account holds against what Google gives it");

        main.Should().Contain(UiText.Dashboard.PoolUsedLabel);
        main.Should().Contain(UiText.Dashboard.WorkspacesHeading);
        main.Should().Contain(UiText.Dashboard.TransfersHeading);
    }

    /// <summary>
    /// The two figures the comp draws and this product does not meter, said in words where the bar
    /// and the chart would be. An empty bar on the operator's home page would read as «the pool is
    /// idle» on the day it stops accepting uploads.
    /// </summary>
    [Fact]
    public async Task An_operators_dashboard_says_what_is_not_metered_instead_of_drawing_it()
    {
        using var harness = new DashboardHarness();
        harness.SeedAccount("A1", PoolAddress);

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(UiText.Dashboard.DailyUploadNotMetered);
        main.Should().Contain(UiText.Dashboard.EgressNotMetered);
    }

    [Fact]
    public async Task An_operators_dashboard_names_what_is_broken()
    {
        using var harness = new DashboardHarness();

        var healthy = harness.SeedAccount("A1", PoolAddress);
        harness.SeedAccount("A2", "archive.cold@gmail.com", GoogleAccountStatus.Disconnected);

        var tenant = harness.SeedWorkspace("Acme");

        var now = DateTimeOffset.UtcNow;

        harness.SeedSession(
            tenant,
            healthy.Id,
            "dataset-full.img",
            UploadSessionStatus.Failed,
            now.AddHours(-2),
            now.AddDays(5),
            "403 userRateLimitExceeded");

        harness.SeedSession(
            tenant,
            healthy.Id,
            "backup-2026-08.tar.zst",
            UploadSessionStatus.InProgress,
            now.AddMinutes(-5),
            now.AddDays(6));

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(UiText.Dashboard.AccountsDisconnected(1));
        main.Should().Contain(UiText.Dashboard.FailuresHeading);
        main.Should().Contain("dataset-full.img");
        main.Should().Contain(
            "403 userRateLimitExceeded",
            "the words the failure arrived in, kept as they arrived — it is what an operator searches for");
    }

    [Fact]
    public async Task An_operator_with_no_connected_account_is_told_so_first()
    {
        using var harness = new DashboardHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/", UriKind.Relative)));

        main.Should().Contain(UiText.Dashboard.NoAccountsHeading);
        main.Should().Contain(UiText.Dashboard.NoAccountsBody);
    }

    /// <summary>
    /// A workspace the claim names and the database does not. A fault rather than a screen of
    /// zeroes: a dashboard of zeroes for a workspace that does not exist is how a broken session
    /// comes to read as a customer with an empty account.
    /// </summary>
    [Fact]
    public async Task A_claim_naming_no_workspace_is_a_fault_and_not_an_empty_dashboard()
    {
        using var harness = new DashboardHarness();

        using var client = harness.NewClient(Guid.NewGuid());
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A signed-in principal with neither claim — an account created before it was given a
    /// workspace. It is refused rather than shown somebody's figures or a screen of zeroes.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_workspace_and_no_pool_is_refused()
    {
        using var harness = new DashboardHarness();

        using var client = harness.NewClientWithoutWorkspace();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
