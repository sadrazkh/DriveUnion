using System.Net;
using DriveUnion.Core.Plans;
using DriveUnion.Tests.Localization;
using DriveUnion.Web.Localization;
using FluentAssertions;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// Taking somebody's access away, and the difference between doing it now and doing it eventually.
///
/// <para>An authentication cookie is a self-contained credential: the server does not go back to the
/// database to ask whether it is still true. Locking the account out closes the front door and does
/// nothing at all to the person who is already inside — which is nearly always the person being
/// disabled. These tests are about that gap.</para>
/// </summary>
[Collection(TenantHostCollection.Name)]
public class DisabledMemberTests
{
    private const string CustomerEmail = "reza@acme.example";

    [Fact]
    public async Task A_disabled_user_is_refused_on_the_next_request_and_not_at_the_next_sign_in()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, userId) = await OnboardAsync(harness, operatorClient);

        using var customer = harness.NewClient();
        (await TenantPanelHarness.SignInAsync(customer, CustomerEmail, TenantPanelHarness.Password))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var before = await customer.GetAsync(new Uri("/files", UriKind.Relative)))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie works before anything happens");
        }

        using var disabled = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members/{userId}/disable",
            new Dictionary<string, string>());

        disabled.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The same client, the same cookie, the very next request. Nothing was slept through and no
        // sign-out happened: the cookie carries the security stamp it was minted with, the disable
        // bumped the stamp on the row, and AddDriveUnionTenancy sets the validation interval to zero
        // so the two are compared on every request instead of twice an hour.
        using var after = await customer.GetAsync(new Uri("/files", UriKind.Relative));

        after.StatusCode.Should().Be(HttpStatusCode.Redirect);
        after.Headers.Location?.ToString().Should().Contain("/Identity/Account/Login");
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in_again_either()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, userId) = await OnboardAsync(harness, operatorClient);

        using var disabled = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members/{userId}/disable",
            new Dictionary<string, string>());

        disabled.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var customer = harness.NewClient();
        using var refused = await TenantPanelHarness.SignInAsync(
            customer, CustomerEmail, TenantPanelHarness.Password);

        // The form comes back rather than a redirect, which is what every refusal looks like here.
        refused.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(refused);
        text.Should().Contain(Said(() => UiText.Identity.LockedOut));
    }

    [Fact]
    public async Task Re_enabling_lets_them_back_in_with_the_same_password()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, userId) = await OnboardAsync(harness, operatorClient);

        using (var disabled = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members/{userId}/disable",
            new Dictionary<string, string>()))
        {
            disabled.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        using (var enabled = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members/{userId}/enable",
            new Dictionary<string, string>()))
        {
            enabled.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        using var customer = harness.NewClient();
        (await TenantPanelHarness.SignInAsync(customer, CustomerEmail, TenantPanelHarness.Password))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var files = await customer.GetAsync(new Uri("/files", UriKind.Relative));
        files.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A reset is the whole of "I have lost my password" in a product with no mail sender and no
    /// self-service reset — and it ends the sessions the old password had open, which is the point
    /// when the reason for the reset is that somebody else knows it.
    /// </summary>
    [Fact]
    public async Task A_reset_password_replaces_the_old_one_and_closes_the_sessions_it_had_open()
    {
        const string Replacement = "Another-Horse-7!";

        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, userId) = await OnboardAsync(harness, operatorClient);

        using var customer = harness.NewClient();
        await TenantPanelHarness.SignInAsync(customer, CustomerEmail, TenantPanelHarness.Password);

        using (var before = await customer.GetAsync(new Uri("/files", UriKind.Relative)))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var reset = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members/{userId}/password",
            new Dictionary<string, string> { ["Password"] = Replacement });

        reset.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var after = await customer.GetAsync(new Uri("/files", UriKind.Relative)))
        {
            after.StatusCode.Should().Be(HttpStatusCode.Redirect, "the old session ends with the old password");
        }

        using var withTheOldOne = harness.NewClient();
        (await TenantPanelHarness.SignInAsync(withTheOldOne, CustomerEmail, TenantPanelHarness.Password))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the old password no longer opens anything");

        using var withTheNewOne = harness.NewClient();
        (await TenantPanelHarness.SignInAsync(withTheNewOne, CustomerEmail, Replacement))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>A workspace with one account in it, made the way an operator makes one.</summary>
    private static async Task<(Guid TenantId, Guid UserId)> OnboardAsync(
        TenantPanelHarness harness,
        HttpClient operatorClient)
    {
        using var created = await TenantPanelHarness.PostAsync(
            operatorClient,
            "/operator/tenants",
            "/operator/tenants",
            new Dictionary<string, string>
            {
                ["Name"] = "Acme Bolts",
                ["Slug"] = "acme-bolts",
                ["PlanCode"] = PlanCatalogue.StandardCode,
            });

        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        using var member = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members",
            new Dictionary<string, string>
            {
                ["Email"] = CustomerEmail,
                ["Password"] = TenantPanelHarness.Password,
            });

        member.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();
        var user = db.Users.Single(u => u.TenantId == tenantId);

        return (tenantId, user.Id);
    }

    private static string Said(Func<string> entry)
    {
        using var english = CultureScope.English();

        return entry();
    }
}
