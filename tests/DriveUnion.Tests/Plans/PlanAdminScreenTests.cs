using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Tests.Localization;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The catalogue as an operator actually works it: through the real pipeline, the real forms and
/// the real antiforgery pair.
///
/// <para>The screen carries two things no service test can see. It has to <b>say</b> that saving a
/// tier moves nobody — the misunderstanding this slice can most easily cause is an operator editing
/// «پایه» and believing every Starter customer just moved. And its refusals have to arrive as
/// sentences rather than as a unique-index error or a constraint violation.</para>
/// </summary>
public class PlanAdminScreenTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public async Task The_tier_form_says_that_saving_it_moves_nobody()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        using var client = harness.NewClient(null, asOperator: true);
        using var response = await client.GetAsync(
            new Uri($"/operator/plans/tiers/{PlanCatalogue.StandardCode}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("ذخیره‌ی این فرم هیچ مشتری‌ای را جابه‌جا نمی‌کند");
        text.Should().Contain(
            "اعداد هر فضای کاری روی ردیف خودش ذخیره شده",
            "the sentence has to say why, not only that");

        // And the honest route for an operator who did mean "move everybody" is on the same card,
        // rather than being something they have to discover.
        text.Should().Contain("اعمال دوباره روی مشتری‌های این پلن");

        // One unit in, one unit out: the field holds 500, not «500 GB» and not «0.5 TB».
        FieldValue(text, "tier-storage").Should().Be("500");
        FieldValue(text, "tier-file").Should().Be("2");
    }

    [Fact]
    public async Task The_form_says_the_same_things_in_english()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true);
        using var response = await client.GetAsync(new Uri(
            $"/operator/plans/tiers/{PlanCatalogue.StandardCode}?lang=en", UriKind.Relative));

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("Saving this form moves no customer");
        text.Should().Contain("No workspace is on this tier yet");
        text.Should().NotContain("ذخیره‌ی این فرم");
    }

    [Fact]
    public async Task An_operator_creates_a_tier_and_its_numbers_land_in_binary_gigabytes()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri("/operator/plans/new", UriKind.Relative));
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = Post(token,
            ("Code", "archive"),
            ("Name", "بایگانی"),
            ("StorageGb", "6144"),
            ("MaxFileGb", "8"),
            ("TrafficGb", "12288"),
            ("Seats", "40"));

        using var response = await client.PostAsync(new Uri("/operator/plans/new", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();
        var created = await db.Plans.AsNoTracking().SingleAsync(p => p.Code == "archive");

        // GB is 1024³ here, because DisplayFormats.Bytes divides by 1024 and a second convention on
        // one screen is how «۱۰۰ GB» typed by an operator becomes «93.1 GB» on a customer's card.
        created.StorageBytes.Should().Be(6144 * Gib);
        created.MonthlyEgressBytes.Should().Be(12288 * Gib);
        created.MaxMembers.Should().Be(40);
        created.SortOrder.Should().BeGreaterThan(30, "a new tier is appended rather than inserted");
    }

    [Fact]
    public async Task The_largest_tier_survives_a_round_trip_through_the_form()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        var address = new Uri($"/operator/plans/tiers/{PlanCatalogue.BusinessCode}", UriKind.Relative);

        using var page = await client.GetAsync(address);
        var text = await LocalizationHarness.TextAsync(page);

        var token = LocalizationHarness.AntiforgeryToken(text);

        // Posted straight back, unedited — which is exactly what an operator does when they open a
        // tier to change its name. 6 TiB reads as «6 TB» everywhere the panel renders a size, and a
        // field pre-filled from that would post 6 and divide the tier by 1024.
        FieldValue(text, "tier-traffic").Should().Be("6144");

        using var form = Post(token,
            ("Code", FieldValue(text, "tier-code")),
            ("Name", FieldValue(text, "tier-name")),
            ("StorageGb", FieldValue(text, "tier-storage")),
            ("MaxFileGb", FieldValue(text, "tier-file")),
            ("TrafficGb", FieldValue(text, "tier-traffic")),
            ("Seats", FieldValue(text, "tier-seats")));

        using var response = await client.PostAsync(address, form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();
        var reread = await db.Plans.AsNoTracking().SingleAsync(p => p.Code == PlanCatalogue.BusinessCode);

        reread.Numbers.Should().Be(
            PlanCatalogue.Business, "a number that goes through the form has to come out unchanged");
    }

    [Fact]
    public async Task Editing_a_tier_through_the_screen_leaves_every_workspace_where_it_was()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        var address = new Uri($"/operator/plans/tiers/{PlanCatalogue.StandardCode}", UriKind.Relative);

        using var page = await client.GetAsync(address);
        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = Post(token,
            ("Code", PlanCatalogue.StandardCode),
            ("Name", "استاندارد"),
            ("StorageGb", "1"),
            ("MaxFileGb", "1"),
            ("TrafficGb", "1"),
            ("Seats", "1"));

        using var response = await client.PostAsync(address, form);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        var tier = await db.Plans.AsNoTracking().SingleAsync(p => p.Code == PlanCatalogue.StandardCode);
        tier.StorageBytes.Should().Be(1 * Gib, "the template really was rewritten");

        var untouched = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);

        // The whole architecture, asserted at the HTTP surface: the numbers are on the workspace's
        // own row and nothing on any enforcement path joins back to the tier.
        untouched.StorageQuotaBytes.Should().Be(PlanCatalogue.Standard.StorageBytes);
        untouched.MaxFileBytes.Should().Be(PlanCatalogue.Standard.MaxFileBytes);
        untouched.MonthlyEgressBytes.Should().Be(PlanCatalogue.Standard.MonthlyEgressBytes);
        untouched.MaxMembers.Should().Be(PlanCatalogue.Standard.MaxMembers);
    }

    [Fact]
    public async Task Re_applying_from_the_screen_moves_the_workspaces_and_records_why()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        var edit = new Uri($"/operator/plans/tiers/{PlanCatalogue.StandardCode}", UriKind.Relative);

        using var page = await client.GetAsync(edit);
        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var edited = Post(token,
            ("Code", PlanCatalogue.StandardCode),
            ("Name", "استاندارد"),
            ("StorageGb", "750"),
            ("MaxFileGb", "4"),
            ("TrafficGb", "2048"),
            ("Seats", "15"));

        using var saved = await client.PostAsync(edit, edited);
        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var reapply = new Uri(
            $"/operator/plans/tiers/{PlanCatalogue.StandardCode}/reapply", UriKind.Relative);

        using var confirmation = await client.GetAsync(reapply);
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);

        var confirmationText = await LocalizationHarness.TextAsync(confirmation);

        // The expensive mistake this button can make, said before it is pressed.
        confirmationText.Should().Contain("هر عدد توافق‌شده‌ای");
        confirmationText.Should().Contain("۱ فضای کاری روی این پلن است");

        using var sweep = Post(
            LocalizationHarness.AntiforgeryToken(confirmationText),
            ("Reason", "Re-priced the standard tier."));

        using var response = await client.PostAsync(reapply, sweep);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        var moved = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        moved.StorageQuotaBytes.Should().Be(750 * Gib);
        moved.MaxMembers.Should().Be(15);

        // Every number that moved, with the reason the operator typed. A silent bulk move is exactly
        // what TenantQuotaChange exists to make impossible.
        var history = await db.TenantQuotaChanges.AsNoTracking()
            .Where(c => c.Reason == "Re-priced the standard tier.")
            .ToListAsync();

        history.Should().HaveCount(4);
        history.Should().OnlyContain(c => c.TenantId == moved.Id);
    }

    [Fact]
    public async Task A_duplicate_code_comes_back_as_a_sentence_and_keeps_what_was_typed()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri("/operator/plans/new", UriKind.Relative));
        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = Post(token,
            ("Code", PlanCatalogue.StarterCode),
            ("Name", "تکراری"),
            ("StorageGb", "100"),
            ("MaxFileGb", "1"),
            ("TrafficGb", "300"),
            ("Seats", "3"));

        using var response = await client.PostAsync(new Uri("/operator/plans/new", UriKind.Relative), form);

        // Re-rendered rather than redirected: six typed values must not be lost to one bad one.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await LocalizationHarness.TextAsync(response);

        text.Should().Contain("پلن دیگری همین کد را دارد");
        text.Should().NotContain("DbUpdateException");
        text.Should().NotContain("UNIQUE constraint");
        FieldValue(text, "tier-name").Should().Be("تکراری");
    }

    [Fact]
    public async Task Retiring_the_default_tier_is_refused_with_a_sentence()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = Post(token, ("Retired", "true"));

        using var response = await client.PostAsync(
            new Uri($"/operator/plans/tiers/{PlanCatalogue.DefaultCode}/retire", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        (await db.Plans.AsNoTracking().SingleAsync(p => p.Code == PlanCatalogue.DefaultCode))
            .IsRetired.Should().BeFalse();

        using var after = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        var text = await LocalizationHarness.TextAsync(after);

        // TenantPlanService throws KeyNotFoundException when the default names nothing, and a 500 at
        // sign-up is not how an operator should learn they broke it.
        text.Should().Contain("پلن پیش‌فرض بازنشسته نمی‌شود");
        text.Should().Contain("Plans:DefaultPlanCode");
    }

    [Fact]
    public async Task Deleting_a_tier_a_workspace_is_on_is_refused_with_a_sentence()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        await harness.Plans().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        var listText = await LocalizationHarness.TextAsync(page);

        // The button is not drawn for a tier somebody is on, and the list says why in a sentence
        // rather than leaving an operator to discover it by pressing something.
        listText.Should().Contain("پلن معمولاً حذف نمی‌شود، بازنشسته می‌شود");

        using var form = Post(LocalizationHarness.AntiforgeryToken(listText));

        using var response = await client.PostAsync(
            new Uri($"/operator/plans/tiers/{PlanCatalogue.StandardCode}/delete", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();
        (await db.Plans.AsNoTracking().AnyAsync(p => p.Code == PlanCatalogue.StandardCode))
            .Should().BeTrue();

        using var after = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        var text = await LocalizationHarness.TextAsync(after);

        text.Should().Contain("دست‌کم یک فضای کاری روی این پلن است");
        text.Should().NotContain("FOREIGN KEY constraint");
    }

    [Fact]
    public async Task A_tier_is_reordered_from_the_list()
    {
        using var harness = new PlanPageHarness();

        using var client = harness.NewClient(null, asOperator: true, keepCookies: true);

        using var page = await client.GetAsync(new Uri("/operator/plans", UriKind.Relative));
        var token = LocalizationHarness.AntiforgeryToken(await LocalizationHarness.TextAsync(page));

        using var form = Post(token, ("Direction", nameof(PlanMove.Up)));

        using var response = await client.PostAsync(
            new Uri($"/operator/plans/tiers/{PlanCatalogue.BusinessCode}/move", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        var order = await db.Plans.AsNoTracking().OrderBy(p => p.SortOrder).Select(p => p.Code).ToListAsync();

        order.Should().ContainInOrder(
            PlanCatalogue.StarterCode, PlanCatalogue.BusinessCode, PlanCatalogue.StandardCode);
    }

    /// <summary>
    /// Every operator plan route, refused for a customer holding the highest thing a customer can
    /// hold — a tenant claim.
    ///
    /// <para>The list is <b>generated from the endpoint table rather than written down</b>, for the
    /// reason M5 §5 gives about its own cross-tenant suite: a route added next month is covered
    /// without anybody remembering to add it here, and a hand-written list is only true on the day
    /// it is written.</para>
    /// </summary>
    [Fact]
    public async Task A_customer_is_refused_every_operator_plan_route()
    {
        using var harness = new PlanPageHarness();
        var (tenant, _, _) = harness.SeedWorkspace("Acme");

        var routes = harness.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .TrimStart('/')
                .StartsWith("operator/plans", StringComparison.Ordinal))
            .ToList();

        routes.Should().HaveCountGreaterThan(
            8, "every route this slice added has to be in the sweep, not a hand-picked few");

        using var client = harness.NewClient(tenant.Id);

        foreach (var route in routes)
        {
            var path = "/" + (route.RoutePattern.RawText ?? string.Empty).TrimStart('/')
                .Replace("{code}", PlanCatalogue.StarterCode, StringComparison.Ordinal)
                .Replace("{tenantId:guid}", tenant.Id.ToString(), StringComparison.Ordinal);

            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()
                ?.HttpMethods ?? ["GET"];

            foreach (var method in methods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);

                // No antiforgery token on the writes, deliberately: authorization runs in middleware
                // before the antiforgery filter, so a 403 here is the policy refusing rather than a
                // missing token, and a customer must never get as far as the token check.
                if (!string.Equals(method, "GET", StringComparison.Ordinal))
                {
                    request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
                }

                using var response = await client.SendAsync(request);

                response.StatusCode.Should().Be(
                    HttpStatusCode.Forbidden,
                    "{0} {1} is an operator route and a customer holding their own tenant claim is not one",
                    method,
                    path);
            }
        }
    }

    /// <summary>The value an input carries, out of the decoded page.</summary>
    private static string FieldValue(string html, string id)
    {
        var match = Regex.Match(
            html,
            $"id=\"{Regex.Escape(id)}\"[^>]*?value=\"([^\"]*)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue($"the form renders no «{id}» field with a value");

        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent Post(string token, params (string Name, string Value)[] fields) =>
        new(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)),
        ]);
}
