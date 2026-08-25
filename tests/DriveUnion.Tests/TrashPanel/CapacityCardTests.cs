using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// The capacity card above the customer's name: the operator's box, and none of the operator's
/// figures.
///
/// <para>The ask was "like the operator". The operator's card reads the daily 750 GB each Google
/// account is allowed — a fact about the pool, and exactly what M1 §1.4 says a customer must never
/// see: not the number, and not that a pool exists at all. <b>The absence is what is worth a
/// test</b>, because a leak here is silent: the page renders, the figures look plausible, and
/// nothing fails.</para>
/// </summary>
public class CapacityCardTests
{
    private const long TwentyGigabytes = 20L * 1024 * 1024 * 1024;

    private const long EighteenMegabytes = 18L * 1024 * 1024;

    /// <summary>
    /// Every screen a customer can open, because the card is the shell's and not one page's. A card
    /// that appeared only where somebody remembered to fill it in would be missing from exactly the
    /// pages a customer is on when they wonder where their space went.
    /// </summary>
    public static TheoryData<string> CustomerScreens() => new("/trash", "/files", "/links", "/plans");

    [Theory]
    [MemberData(nameof(CustomerScreens))]
    public async Task The_card_renders_the_tenants_own_numbers(string path)
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: TwentyGigabytes);
        harness.SeedTrashedFile(tenant, "invoice.pdf", EighteenMegabytes, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var sidebar = PanelMarkup.Decode(
            PanelMarkup.Sidebar(await client.GetStringAsync(new Uri(path, UriKind.Relative))));

        sidebar.Should().Contain(UiText.Capacity.StorageLabel);
        sidebar.Should().Contain(
            UiText.Plans.OfCap(DisplayFormats.Bytes(TwentyGigabytes), DisplayFormats.Bytes(TrashPanelHarness.StorageQuotaBytes)),
            "storage spent against this workspace's own cap is the first thing the card is for");

        sidebar.Should().Contain(UiText.Capacity.TrafficLabel);
        sidebar.Should().Contain(
            UiText.Capacity.TrafficOfCap(
                DisplayFormats.Bytes(0),
                DisplayFormats.Bytes(TrashPanelHarness.MonthlyEgressBytes)),
            "spent against the allowance this workspace bought — nothing served yet, so zero, which "
            + "is a figure the meter stands behind rather than a placeholder");

        sidebar.Should().Contain(UiText.Capacity.TrashLabel);
        sidebar.Should().Contain(
            DisplayFormats.Bytes(EighteenMegabytes),
            "the trash's size is the difference between what a customer believes they freed and what "
            + "they actually did, which is why it is on a capacity card at all");
    }

    [Theory]
    [MemberData(nameof(CustomerScreens))]
    public async Task The_card_never_carries_the_pools_daily_figure(string path)
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: TwentyGigabytes);
        harness.SeedTrashedFile(tenant, "invoice.pdf", EighteenMegabytes, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id);
        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));
        var sidebar = PanelMarkup.Decode(PanelMarkup.Sidebar(html));

        sidebar.Should().NotContain(
            UiText.Shell.TodaysUploadQuota,
            "«سهمیه آپلود امروز» is the operator's daily allowance across their Google accounts, and "
            + "a customer must not learn that a pool exists");

        sidebar.Should().NotContain("750", "the daily per-account figure Google allows the operator");

        // The other two halves of the same card, absent for the same reason: the pool's account
        // count and its total capacity, and the address of the account holding this file.
        sidebar.Should().NotContain("brand-sub");
        sidebar.Should().NotContain("@gmail.com");
    }

    /// <summary>
    /// The positive control for the assertion above.
    ///
    /// <para>Without it, «the customer's sidebar does not contain the operator's label» would pass on
    /// a panel where that label had been renamed, deleted, or never rendered for anybody — which is
    /// a guard that proves nothing at all.</para>
    /// </summary>
    [Fact]
    public async Task The_operators_own_card_is_exactly_where_it_was()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        var sidebar = PanelMarkup.Decode(
            PanelMarkup.Sidebar(await client.GetStringAsync(new Uri("/operator/settings", UriKind.Relative))));

        sidebar.Should().Contain(
            UiText.Shell.TodaysUploadQuota,
            "the operator's card is unchanged by this slice, which is half of what «keep it exactly "
            + "as it is» means");

        // …and the customer's card is not drawn for them: an operator has no workspace, so the
        // figures would be nobody's.
        sidebar.Should().NotContain(UiText.Capacity.TrashLabel);
        sidebar.Should().NotContain(UiText.Capacity.TrafficLabel);
    }

    [Fact]
    public async Task Emptying_the_trash_moves_the_figure_on_the_card()
    {
        using var harness = new TrashPanelHarness();

        var tenant = harness.SeedWorkspace("Acme", storageUsedBytes: EighteenMegabytes);
        harness.SeedTrashedFile(tenant, "invoice.pdf", EighteenMegabytes, DateTimeOffset.UtcNow.AddDays(30));

        using var client = harness.NewClient(tenant.Id, keepCookies: true);

        var before = PanelMarkup.Decode(
            PanelMarkup.Sidebar(await client.GetStringAsync(new Uri("/trash", UriKind.Relative))));

        before.Should().Contain(DisplayFormats.Bytes(EighteenMegabytes));

        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");
        using var response = await TrashPanelHarness.PostAsync(client, "/trash/empty", token);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);

        var after = PanelMarkup.Decode(
            PanelMarkup.Sidebar(await client.GetStringAsync(new Uri("/trash", UriKind.Relative))));

        // Both figures move together, and that is the whole point of putting them on one card: the
        // bytes left the trash and left the workspace's usage at the same moment.
        after.Should().NotContain(DisplayFormats.Bytes(EighteenMegabytes));
        after.Should().Contain(UiText.Plans.OfCap(
            DisplayFormats.Bytes(0),
            DisplayFormats.Bytes(TrashPanelHarness.StorageQuotaBytes)));
    }

    /// <summary>
    /// A customer whose claim names a workspace the database does not have gets no card rather than
    /// a card of zeroes. A panel that renders zeroes for a workspace that does not exist is how a
    /// broken session comes to read as a customer with an empty account.
    /// </summary>
    [Fact]
    public async Task A_claim_naming_no_workspace_draws_no_card()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(Guid.NewGuid());
        var sidebar = PanelMarkup.Decode(
            PanelMarkup.Sidebar(await client.GetStringAsync(new Uri("/trash", UriKind.Relative))));

        sidebar.Should().NotContain(UiText.Capacity.StorageLabel);
        sidebar.Should().NotContain(UiText.Shell.TodaysUploadQuota);
    }
}
