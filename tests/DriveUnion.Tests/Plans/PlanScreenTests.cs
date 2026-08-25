using System.Globalization;
using System.Net;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Tests.Localization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The plan screens as the real pipeline renders them, for the three audiences: the customer whose
/// numbers they are, the operator who sells them, and the stranger who is told nothing.
/// </summary>
public class PlanScreenTests
{
    [Fact]
    public async Task The_customers_card_names_the_limit_that_would_refuse_them()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/plans", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("پلن و مصرف");
        text.Should().Contain("2 GB", "the per-file limit is the number an upload will be refused against");
        text.Should().Contain("500 GB", "the storage cap is what the meter is measured against");

        // A refusal that does not name the limit generates a support ticket every time, so the rule
        // is on the screen before anybody hits it — and it carries no uploader link, because over
        // this number no uploader in the product would take the file either.
        text.Should().Contain("از هیچ راهی ذخیره نمی‌شود");
    }

    [Fact]
    public async Task The_card_says_the_same_things_in_english()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/plans?lang=en", UriKind.Relative));

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("Plan and usage");
        text.Should().Contain("Largest file");
        text.Should().Contain("will not be stored by any route");
        text.Should().NotContain("پلن و مصرف");
    }

    [Fact]
    public async Task Over_the_storage_cap_the_card_says_what_that_does_and_does_not_mean()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await using var db = harness.NewDbContext();
        await TenantStorageMeter.TryReserveAsync(db, tenant.Id, 50L * 1024 * 1024 * 1024, default);
        await harness.Plans().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 10L * 1024 * 1024 * 1024, "Downgraded.", null, default);

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/plans", UriKind.Relative));

        var text = await LocalizationHarness.TextAsync(response);

        // Said in words rather than only in red: uploads stop, nothing is deleted, and the way out is
        // deleting files — which needs the panel, which still works.
        text.Should().Contain("هیچ فایلی حذف نشده");
        text.Should().Contain("bar-fill--danger");
    }

    [Fact]
    public async Task There_is_no_upgrade_button_because_there_is_nowhere_for_it_to_go()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(new Uri("/plans", UriKind.Relative));

        var text = await LocalizationHarness.TextAsync(response);

        // There is no checkout, so the customer's screen offers no route to a plan change — not the
        // operator's, and not one of its own. What it does say is who changes a plan.
        text.Should().NotContain("/operator/", "a customer's screen must not know that surface exists");
        text.Should().Contain("تغییر پلن از طریق ما انجام می‌شود");
    }

    [Fact]
    public async Task An_anonymous_caller_is_not_shown_a_plan()
    {
        using var harness = new PlanPageHarness();
        harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(null);
        using var response = await client.GetAsync(new Uri("/plans", UriKind.Relative));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_customer_cannot_reach_the_operators_catalogue()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(tenant.Id);

        using var catalogue = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        catalogue.StatusCode.Should().NotBe(HttpStatusCode.OK);

        // Including their own workspace's operator page: the route surface is the control, not the
        // tenant on it, and a customer holding their own id must not be able to walk in through it.
        using var own = await client.GetAsync(
            new Uri($"/operator/plans/tenants/{tenant.Id}", UriKind.Relative));
        own.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stranger_on_a_share_link_learns_nothing_about_the_customers_plan()
    {
        using var harness = new PlanPageHarness();
        var (tenant, link, _) = harness.SeedWorkspace("Acme");

        await using var db = harness.NewDbContext();
        await TenantStorageMeter.TryReserveAsync(db, tenant.Id, 50L * 1024 * 1024 * 1024, default);
        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.BusinessCode, "Business.", null, default);

        using var client = harness.NewClient(null);
        using var response = await client.GetAsync(new Uri($"/d/{link.Slug}", UriKind.Relative));

        // The link still works. Being over a cap costs a tenant their uploads and nothing else, and
        // §4's four-cause refusal card gains no fifth cause.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers)
            .Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));

        var everything = raw + '\n' + headers;

        // Asserted on the raw string rather than on a deserialised object, because the bug being
        // guarded against is a view model gaining a property.
        foreach (var forbidden in new[]
                 {
                     tenant.Id.ToString(),
                     tenant.Id.ToString("N"),
                     tenant.Id.ToString().ToUpperInvariant(),
                     PlanCatalogue.BusinessCode,
                     PlanCatalogue.Business.StorageBytes.ToString(CultureInfo.InvariantCulture),
                     PlanCatalogue.Business.MaxFileBytes.ToString(CultureInfo.InvariantCulture),
                 })
        {
            everything.Should().NotContain(
                forbidden,
                "two slugs sharing a tenant id is a correlator that says «same customer», and a plan "
                + "state is commercial information about somebody who did not choose to publish it");
        }
    }

    [Fact]
    public async Task The_operators_screen_says_the_figures_are_not_final()
    {
        using var harness = new PlanPageHarness();
        harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(null, asOperator: true);
        using var response = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        // An operator reads a number off this table and quotes it to a customer. The warning has to
        // be where that happens.
        text.Should().Contain("این اعداد موقت‌اند و تأیید نشده‌اند");
        text.Should().Contain("starter");
        text.Should().Contain("business");

        // Over-commitment is displayed rather than prevented, and traffic gets no pool comparison
        // because the box's egress ceiling is a bandwidth number nobody has measured.
        text.Should().Contain("تعهدشده");
        text.Should().Contain("ترافیک فروخته‌شده");
    }

    [Fact]
    public async Task The_operator_sees_a_preview_before_confirming_a_downgrade()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme", fileBytes: 4L * 1024 * 1024 * 1024);

        await using var db = harness.NewDbContext();
        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.BusinessCode, "Business.", null, default);
        await TenantStorageMeter.TryReserveAsync(
            db, tenant.Id, PlanCatalogue.Starter.StorageBytes + 1, default);

        using var client = harness.NewClient(null, asOperator: true);
        using var response = await client.GetAsync(new Uri(
            $"/operator/plans/tenants/{tenant.Id}?plan={PlanCatalogue.StarterCode}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("نتیجه‌ی این تغییر");
        text.Should().Contain("بیشتر از سقف فضا خواهد بود");
        text.Should().Contain("فایل بزرگ‌تر از", "the count is what makes «none of them breaks» convincing");
        text.Should().Contain("کاهش سقف فقط کار بعدی را محدود می‌کند");
    }

    [Fact]
    public async Task Applying_a_plan_from_the_operators_screen_writes_the_history_row()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri(
            $"/operator/plans/tenants/{tenant.Id}", UriKind.Relative));

        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("PlanCode", PlanCatalogue.BusinessCode),
            new KeyValuePair<string, string>("Reason", "Upgraded after the call."),
        ]);

        using var response = await client.PostAsync(
            new Uri($"/operator/plans/tenants/{tenant.Id}", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        var moved = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        moved.StorageQuotaBytes.Should().Be(PlanCatalogue.Business.StorageBytes);
        moved.MaxFileBytes.Should().Be(PlanCatalogue.Business.MaxFileBytes);

        var history = await db.TenantQuotaChanges.AsNoTracking()
            .Where(c => c.TenantId == tenant.Id)
            .ToListAsync();

        history.Should().NotBeEmpty();
        history.Should().OnlyContain(c => c.Reason == "Upgraded after the call.");

        // The operator who pressed the button, named.
        //
        // This asserted null and explained that the test principal carried no identity id — which
        // documented a gap in the harness as though it were a property of the product. The handler
        // reads NameIdentifier, a real cookie always carries one, and the harness now mints one too,
        // so the audit trail says who moved this workspace between plans. That is the thing the
        // column exists for, and until now nothing checked it was ever written.
        history.Should().OnlyContain(c => c.ChangedByUserId == PlanPageHarness.UserId);
    }

    [Fact]
    public async Task A_change_with_no_reason_is_refused()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri(
            $"/operator/plans/tenants/{tenant.Id}", UriKind.Relative));

        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("PlanCode", PlanCatalogue.BusinessCode),
            new KeyValuePair<string, string>("Reason", "   "),
        ]);

        using var response = await client.PostAsync(
            new Uri($"/operator/plans/tenants/{tenant.Id}", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        // A quota change with no reason is the one a support conversation cannot use, and the whole
        // table exists for support conversations.
        (await db.TenantQuotaChanges.AsNoTracking().CountAsync()).Should().Be(0);

        var untouched = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        untouched.StorageQuotaBytes.Should().Be(PlanCatalogue.Default.StorageBytes);
    }
}
