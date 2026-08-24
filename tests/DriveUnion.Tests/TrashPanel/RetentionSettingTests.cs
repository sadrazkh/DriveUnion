using System.Globalization;
using System.Net;
using DriveUnion.Core.Settings;
using DriveUnion.Web.Localization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.TrashPanel;

/// <summary>
/// «مهلت نگهداری در سطل زباله» — the operator's one knob, and who may turn it.
///
/// <para>It is an operator setting because it is a decision about the operator's own storage: how
/// long their pool carries bytes a customer has already deleted. A customer must meet a 403 rather
/// than a number they cannot change — and the screen has to say, where the operator is typing, that
/// the window reaches only what is deleted from now on.</para>
/// </summary>
public class RetentionSettingTests
{
    [Fact]
    public async Task A_tenant_user_cannot_open_the_retention_setting()
    {
        using var harness = new TrashPanelHarness();
        var tenant = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/operator/settings", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "retention is a decision about the operator's storage, and a hidden link is not an "
            + "access control");
    }

    [Fact]
    public async Task An_operator_opens_it_and_reads_what_it_does_and_does_not_do()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        using var response = await client.GetAsync(new Uri("/operator/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var main = PanelMarkup.MainContent(await response.Content.ReadAsStringAsync());

        main.Should().Contain(UiText.OperatorSettings.RetentionHeading);

        // The half an operator will otherwise assume, and the half that destroys files early.
        main.Should().Contain(UiText.OperatorSettings.RetentionAppliesForward);
        main.Should().Contain(UiText.OperatorSettings.RetentionAndEmptying);

        main.Should().Contain(UiText.OperatorSettings.RetentionBounds(
            OperatorSettings.MinimumTrashRetentionDays,
            OperatorSettings.MaximumTrashRetentionDays));

        // The box opens on the value in force, which the migration seeds at Drive's own number.
        main.Should().Contain(string.Create(
            CultureInfo.InvariantCulture,
            $"value=\"{OperatorSettings.DefaultTrashRetentionDays}\""));
    }

    [Fact]
    public async Task An_operator_stores_a_new_window()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/operator/settings");

        using var response = await TrashPanelHarness.PostAsync(
            client,
            "/operator/settings/retention",
            token,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Days"] = "7" });

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();
        var row = await db.OperatorSettings.SingleAsync(s => s.Id == OperatorSettings.SingletonId);

        row.TrashRetentionDays.Should().Be(7);
        row.UpdatedAt.Should().NotBeNull("the one setting that decides when somebody's bytes are destroyed");
        row.UpdatedByUserId.Should().NotBeNull("and who decided it");

        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/operator/settings", UriKind.Relative)));
        main.Should().Contain(UiText.OperatorSettings.Saved(7));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("-5")]
    public async Task A_window_outside_the_bounds_is_refused_and_nothing_is_written(string days)
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true, keepCookies: true);
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/operator/settings");

        using var response = await TrashPanelHarness.PostAsync(
            client,
            "/operator/settings/retention",
            token,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Days"] = days });

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();
        var row = await db.OperatorSettings.SingleAsync(s => s.Id == OperatorSettings.SingletonId);

        row.TrashRetentionDays.Should().Be(
            OperatorSettings.DefaultTrashRetentionDays,
            "a form that silently clamped a typed 366 to 365 would tell an operator they had set "
            + "something they had not");
        row.UpdatedAt.Should().BeNull();

        var main = PanelMarkup.MainContent(await client.GetStringAsync(new Uri("/operator/settings", UriKind.Relative)));
        main.Should().Contain(UiText.OperatorSettings.RefusedOutOfRange(
            OperatorSettings.MinimumTrashRetentionDays,
            OperatorSettings.MaximumTrashRetentionDays));
    }

    [Fact]
    public async Task A_get_cannot_change_the_window()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true);
        using var response = await client.GetAsync(
            new Uri("/operator/settings/retention?Days=1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        await using var db = harness.NewDbContext();
        (await db.OperatorSettings.SingleAsync(s => s.Id == OperatorSettings.SingletonId))
            .TrashRetentionDays.Should().Be(OperatorSettings.DefaultTrashRetentionDays);
    }

    [Fact]
    public async Task A_post_without_the_token_is_refused()
    {
        using var harness = new TrashPanelHarness();

        using var client = harness.NewClient(tenantId: null, asOperator: true, keepCookies: true);
        await client.GetStringAsync(new Uri("/operator/settings", UriKind.Relative));

        using var response = await client.PostAsync(
            new Uri("/operator/settings/retention", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Days"] = "1",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = harness.NewDbContext();
        (await db.OperatorSettings.SingleAsync(s => s.Id == OperatorSettings.SingletonId))
            .TrashRetentionDays.Should().Be(OperatorSettings.DefaultTrashRetentionDays);
    }

    [Fact]
    public async Task A_tenant_user_cannot_post_the_window_either()
    {
        using var harness = new TrashPanelHarness();
        var tenant = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id, keepCookies: true);

        // The token comes from a page this caller is allowed to open, which is the strongest form of
        // the question: a valid token and the wrong claim still has to be a refusal.
        var token = await TrashPanelHarness.AntiforgeryTokenAsync(client, "/trash");

        using var response = await TrashPanelHarness.PostAsync(
            client,
            "/operator/settings/retention",
            token,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Days"] = "1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var db = harness.NewDbContext();
        (await db.OperatorSettings.SingleAsync(s => s.Id == OperatorSettings.SingletonId))
            .TrashRetentionDays.Should().Be(OperatorSettings.DefaultTrashRetentionDays);
    }

    /// <summary>
    /// The nav entry follows the same claim the route does. A hidden link is not an access control —
    /// which is why the route is tested first — but a customer must not read the words of an
    /// operator's screen in their own markup either.
    /// </summary>
    [Fact]
    public async Task The_setting_is_in_the_operators_menu_and_not_in_a_customers()
    {
        using var harness = new TrashPanelHarness();
        var tenant = harness.SeedWorkspace("Acme");

        using var asOperator = harness.NewClient(tenantId: null, asOperator: true);
        using var asCustomer = harness.NewClient(tenant.Id);

        var operatorSidebar = PanelMarkup.Sidebar(
            await asOperator.GetStringAsync(new Uri("/operator/settings", UriKind.Relative)));

        var customerSidebar = PanelMarkup.Sidebar(
            await asCustomer.GetStringAsync(new Uri("/trash", UriKind.Relative)));

        operatorSidebar.Should().Contain("/operator/settings");
        customerSidebar.Should().NotContain("/operator/settings");

        // …and the trash is the other way round: a workspace's screen, and the operator has none.
        customerSidebar.Should().Contain("/trash");
        operatorSidebar.Should().NotContain("\"/trash\"");
    }
}
