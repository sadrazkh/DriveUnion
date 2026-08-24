using System.Net;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Tests.Localization;
using DriveUnion.Web.Localization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// Onboarding a customer, over HTTP, through the pipeline the panel actually boots.
///
/// <para>The service tests beside these prove the rules. This proves the thing a service test cannot
/// see: that the form's field names are the ones the action binds, that the antiforgery pair is
/// intact, that the views render, and — the whole point of the slice — that the account the operator
/// created can be signed into and reaches its own files.</para>
/// </summary>
[Collection(TenantHostCollection.Name)]
public class TenantOnboardingTests
{
    private const string CustomerEmail = "reza@acme.example";

    [Fact]
    public async Task An_operator_creates_a_workspace_and_its_first_user_who_then_signs_in_and_reaches_their_files()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

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

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        // Straight to the workspace, because without an account nobody can sign in and the workspace
        // is inert — that is the next thing an operator has to do, every time.
        using var workspace = await operatorClient.GetAsync(
            new Uri($"/operator/tenants/{tenantId}", UriKind.Relative));

        workspace.StatusCode.Should().Be(HttpStatusCode.OK);

        var workspaceText = await LocalizationHarness.TextAsync(workspace);
        workspaceText.Should().Contain("acme-bolts", "the slug is the folder every upload lands in");
        workspaceText.Should().Contain(Said(() => UiText.Tenants.NoMembers));

        using var member = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members",
            new Dictionary<string, string>
            {
                ["Email"] = CustomerEmail,
                ["DisplayName"] = "Reza",
                ["Password"] = TenantPanelHarness.Password,
            });

        member.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // ── And now the part none of this is worth anything without ──────────────────────────────
        using var customer = harness.NewClient();

        using var signedIn = await TenantPanelHarness.SignInAsync(
            customer, CustomerEmail, TenantPanelHarness.Password);

        signedIn.StatusCode.Should().Be(
            HttpStatusCode.Redirect, "a 200 here is the sign-in form coming back with a refusal on it");

        using var files = await customer.GetAsync(new Uri("/files", UriKind.Relative));

        files.StatusCode.Should().Be(HttpStatusCode.OK);

        // Their own panel, and none of the operator's. M1 §1.4: a customer must never learn that a
        // pool of Google accounts exists, let alone which one holds their file.
        var filesText = await LocalizationHarness.TextAsync(files);
        filesText.Should().NotContain("acme-bolts", "the slug is operator vocabulary");
        filesText.Should().NotContain("/operator/tenants");
    }

    /// <summary>
    /// A duplicate slug is a sentence about folders, not a unique-index violation. Two workspaces
    /// sharing one slug would put two customers' files in one directory inside every Drive account
    /// in the pool, and nothing downstream could untangle them.
    /// </summary>
    [Fact]
    public async Task A_duplicate_slug_is_refused_with_a_sentence()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var first = await CreateAsync(client, "Acme Bolts", "acme-bolts");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var second = await CreateAsync(client, "Acme Nuts", "acme-bolts");

        // Re-rendered rather than redirected: a slug is a permanent choice somebody spent a minute
        // on, and clearing the form to say "that one is taken" is how they paste one they had not
        // finished thinking about.
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(second);

        text.Should().Contain(Said(() => UiText.Tenants.SlugTaken("acme-bolts")));
        text.Should().NotContain("IX_Tenants_Slug", "a database error is not a sentence");
        text.Should().NotContain("SqliteException");

        // The name they typed survives the refusal.
        text.Should().Contain("Acme Nuts");

        await using var db = harness.NewDbContext();
        (await db.Tenants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_slug_that_is_not_a_folder_name_is_refused_and_the_rule_is_shown()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var response = await CreateAsync(client, "Acme Bolts", "Acme Bolts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain(Said(() => UiText.Tenants.SlugMalformed));
        text.Should().Contain(Said(() => UiText.Tenants.SlugRule(TenantSlug.MinimumLength, TenantSlug.MaximumLength)));

        await using var db = harness.NewDbContext();
        (await db.Tenants.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// The refusal <c>Tenant.MaxMembers</c> exists for. The account must not be created and then
    /// apologised for — an account that exists can sign in.
    /// </summary>
    [Fact]
    public async Task The_member_cap_refuses_the_user_that_would_exceed_it()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var created = await CreateAsync(client, "Acme Bolts", "acme-bolts");
        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        // One seat, set on the row the way the plans screen sets it. Doing it here rather than
        // creating three accounts keeps the test about the cap rather than about the tier's number.
        await using (var db = harness.NewDbContext())
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.MaxMembers = 1;
            await db.SaveChangesAsync();
        }

        using var first = await AddMemberAsync(client, tenantId, "one@acme.example");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var refused = await AddMemberAsync(client, tenantId, "two@acme.example");

        refused.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(refused);
        text.Should().Contain(Said(() => UiText.Tenants.SeatsFull(1, 1)));

        await using var check = harness.NewDbContext();

        check.Users.Count(u => u.TenantId == tenantId).Should().Be(1);

        // Not merely "the count did not go up": the address must not exist anywhere, or it is held
        // against Identity's unique index by an account nobody meant to make.
        (await check.Users.AnyAsync(u => u.NormalizedEmail == "TWO@ACME.EXAMPLE")).Should().BeFalse();
    }

    [Fact]
    public async Task The_workspace_list_shows_each_customers_slug_plan_seats_and_usage()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var created = await CreateAsync(client, "Acme Bolts", "acme-bolts");
        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        using (var member = await AddMemberAsync(client, tenantId, "one@acme.example"))
        {
            member.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        using var list = await client.GetAsync(new Uri("/operator/tenants", UriKind.Relative));

        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(list);

        text.Should().Contain("Acme Bolts");
        text.Should().Contain("acme-bolts");
        text.Should().Contain(PlanCatalogue.StandardCode);
        text.Should().Contain(Said(() => UiText.Tenants.MembersOfCap(1, PlanCatalogue.Standard.MaxMembers)));

        // The permanence warning is at the field, not in a document, because the field is where
        // somebody chooses a folder name they will never be able to change.
        text.Should().Contain(Said(() => UiText.Tenants.SlugIsPermanent));
    }

    /// <summary>
    /// There is no delete button, and the screen says why. Nothing in this schema has a foreign key
    /// from a tenant's rows back to <c>Tenants</c>, so deleting the row would not fail — it would
    /// orphan every file, link and account that named it while the bytes stayed in Drive.
    /// </summary>
    [Fact]
    public async Task The_workspace_page_offers_no_deletion_and_says_what_to_do_instead()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var created = await CreateAsync(client, "Acme Bolts", "acme-bolts");
        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        using var page = await client.GetAsync(new Uri($"/operator/tenants/{tenantId}", UriKind.Relative));

        var html = await page.Content.ReadAsStringAsync();

        html.Should().NotContain($"/operator/tenants/{tenantId}/delete");

        var text = await LocalizationHarness.TextAsync(page);
        text.Should().Contain(Said(() => UiText.Tenants.NoDeletionHeading));
        text.Should().Contain(Said(() => UiText.Tenants.NoDeletionBody));

        // Moving a quota has exactly one writer and it is the plans screen, so this page links to it
        // rather than growing a second one. Asserted because Url.Action renders an empty href rather
        // than failing when the action it names has been renamed out from under it.
        html.Should().Contain($"/operator/plans/tenants/{tenantId}");
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_a_404_rather_than_an_empty_page()
    {
        using var harness = new TenantPanelHarness();
        using var client = await harness.SignedInOperatorAsync();

        using var response = await client.GetAsync(
            new Uri($"/operator/tenants/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string slug) =>
        TenantPanelHarness.PostAsync(
            client,
            "/operator/tenants",
            "/operator/tenants",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["Slug"] = slug,
                ["PlanCode"] = PlanCatalogue.StandardCode,
            });

    private static Task<HttpResponseMessage> AddMemberAsync(
        HttpClient client,
        Guid tenantId,
        string email) =>
        TenantPanelHarness.PostAsync(
            client,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members",
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = TenantPanelHarness.Password,
            });

    /// <summary>
    /// The catalogue's own sentence, in the language the harness's clients ask for. Asserting on the
    /// entry rather than on a transcription of it means a reworded sentence stays green and a
    /// deleted one goes red, which is the right way round.
    /// </summary>
    private static string Said(Func<string> entry)
    {
        using var english = CultureScope.English();

        return entry();
    }
}
