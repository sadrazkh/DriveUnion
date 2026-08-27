using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DriveUnion.Core.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// «اعلان‌ها»: the screen, and the two calls the page makes.
///
/// <para><b>The order of what it says is the argument.</b> A reader deciding whether to let a server
/// write on their lock screen is entitled to know what it will write there <i>first</i>; a promise
/// made underneath a control somebody has already pressed is not a promise. So the card that says
/// what gets sent is rendered above the button, and the assertion below is that it is there at all
/// rather than that it is in a particular place.</para>
///
/// <para><b>And the control is not offered where it cannot work.</b> A deployment with no VAPID keys
/// draws no subscribe button: a browser will mint a subscription against any 65 bytes, every send to
/// it would be a 403 for the life of the row, and the reader would have given a permission that
/// buys them nothing.</para>
/// </summary>
public class NotificationsScreenTests
{
    /// <summary>The mount point's props, which is how the page is handed everything it cannot know.</summary>
    private static readonly Regex MountPoint = new(
        """<div class="card"\s+data-island="notifications"(?<attributes>[^>]*)>""",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    // ── who can reach it ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both audiences, at one address.
    ///
    /// <para>Not the tenant policy and not the operator policy. A customer subscribes to hear about
    /// their own link-uploads and deletions; an operator subscribes to hear about a new abuse
    /// report, and an operator has no workspace at all — so a tenant policy here would lock them out
    /// of the one notification that is racing Google.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_a_customer_and_an_operator_with_no_workspace_can_open_it(bool asOperator)
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(asOperator ? null : tenant.Id, asOperator);
        using var response = await client.GetAsync(new Uri("/notifications", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_out_visitor_is_challenged_rather_than_shown_it()
    {
        await using var harness = new NotificationsPageHarness();
        harness.SeedWorkspace();

        using var client = harness.NewClient(tenantId: null);
        using var response = await client.GetAsync(new Uri("/notifications", UriKind.Relative));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    // ── what the screen says before it asks ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>What will be sent is said before permission is asked for.</b>
    ///
    /// <para>The one sentence this whole payload design exists to be able to write truthfully. If it
    /// ever stops being on the screen, the reader is being asked to accept something nobody told
    /// them about.</para>
    /// </summary>
    [Fact]
    public async Task The_screen_says_what_will_be_sent_before_it_offers_the_control()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var markup = await MarkupAsync(harness, tenant.Id);

        // Razor encodes everything outside Basic Latin, so the Persian has to be decoded before it
        // can be compared with itself.
        var text = WebUtility.HtmlDecode(markup);

        text.Should().Contain("هیچ نام فایلی", "the promise is made before the button, not after it");
        text.Should().Contain("رمز می‌شود", "and so is the fact that the push service cannot read it");

        text.IndexOf("هیچ نام فایلی", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("data-notifications-enable", StringComparison.Ordinal),
            "a promise underneath a control somebody has already pressed is not a promise");
    }

    /// <summary>
    /// The three things that are notified are listed, and the omission is explained.
    ///
    /// <para>A reader who is not told why their uploads are silent concludes the feature is broken
    /// and turns it off — taking the abuse report with it, for an operator.</para>
    /// </summary>
    [Fact]
    public async Task The_screen_lists_what_is_notified_and_says_what_is_not()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var text = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id));

        text.Should().Contain("آپلود از روی لینک");
        text.Should().Contain("حذف گروهی");
        text.Should().Contain("آپلود معمولی", "the omission is stated rather than left to be guessed");
    }

    /// <summary>
    /// The abuse line is the operator's and is absent for a customer.
    ///
    /// <para>Absent rather than hidden, the rule the whole panel keeps: a customer must not be able
    /// to tell from this markup that an abuse queue exists.</para>
    /// </summary>
    [Fact]
    public async Task Only_the_operator_is_told_about_the_abuse_notification()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var customer = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id));
        var operatorView = WebUtility.HtmlDecode(await MarkupAsync(harness, null, asOperator: true));

        operatorView.Should().Contain("گزارش تخلف تازه");
        customer.Should().NotContain("گزارش تخلف");
    }

    /// <summary>Both languages, like every other screen in the panel.</summary>
    [Fact]
    public async Task The_screen_speaks_the_panels_language()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var persian = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id));
        var english = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id, culture: "en"));

        persian.Should().Contain("اعلان‌ها");
        english.Should().Contain("Notifications");
        english.Should().NotContain("اعلان‌ها");
    }

    // ── the control, and the states it is not offered in ────────────────────────────────────────

    /// <summary>
    /// <b>Keys configured: the page carries the application server key and the two addresses.</b>
    ///
    /// <para>The key is the browser's <c>applicationServerKey</c> and has to be exactly the one the
    /// server will sign with — a mismatch is a 403 from the push service for the life of every
    /// subscription minted against it.</para>
    /// </summary>
    [Fact]
    public async Task A_configured_deployment_hands_the_page_the_key_and_the_two_addresses()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var attributes = MountPoint.Match(await MarkupAsync(harness, tenant.Id));

        attributes.Success.Should().BeTrue("a configured deployment draws the control");

        var props = attributes.Groups["attributes"].Value;

        props.Should().Contain($@"data-application-server-key=""{harness.PublicKey}""");
        props.Should().Contain(@"data-subscribe-url=""/api/notifications/subscribe""");
        props.Should().Contain(@"data-unsubscribe-url=""/api/notifications/unsubscribe""");

        // Minted per response and written into the markup, exactly as the upload queue's is: a
        // bundle compiled once cannot know it, and a POST without it is a 400 that reads like a
        // broken feature.
        props.Should().Contain(@"data-antiforgery-header=""RequestVerificationToken""");
        props.Should().MatchRegex(@"data-antiforgery-token=""[^""]{20,}""");
    }

    /// <summary>
    /// <b>No keys, no control.</b>
    ///
    /// <para>The mount point is absent rather than disabled: a browser would mint a subscription
    /// against any 65 bytes and the reader would have given a permission that no notification could
    /// arrive through.</para>
    /// </summary>
    [Fact]
    public async Task An_unconfigured_deployment_draws_no_control_at_all()
    {
        await using var harness = new NotificationsPageHarness(configured: false);
        var tenant = harness.SeedWorkspace();

        var markup = await MarkupAsync(harness, tenant.Id);

        MountPoint.IsMatch(markup).Should().BeFalse();
        markup.Should().NotContain("data-notifications-enable");

        WebUtility.HtmlDecode(markup).Should().Contain(
            "اپراتور هنوز کلیدهای اعلان",
            "the reader is told this is not set up rather than shown a button that cannot work");
    }

    /// <summary>
    /// The configuration keys are named for the operator and for nobody else.
    ///
    /// <para><c>Push:PublicKey</c> is the deployment's own vocabulary. A customer is told the
    /// operator has not set this up — true, actionable for them, and saying nothing about how the
    /// deployment is configured.</para>
    /// </summary>
    [Fact]
    public async Task Only_the_operator_is_told_which_settings_are_missing()
    {
        await using var harness = new NotificationsPageHarness(configured: false);
        var tenant = harness.SeedWorkspace();

        (await MarkupAsync(harness, null, asOperator: true)).Should().Contain("Push:PublicKey");
        (await MarkupAsync(harness, tenant.Id)).Should().NotContain("Push:");
    }

    /// <summary>
    /// The four sentences the script writes come out of <c>UiText</c> and travel in the markup.
    ///
    /// <para>A bundle is compiled once and cannot ask which language a request was in — which is how
    /// a copy button once came to answer an English panel in Persian. A literal in the
    /// <c>.ts</c> would be a sentence one of the two panels could not say.</para>
    /// </summary>
    [Fact]
    public async Task The_sentences_the_script_writes_are_rendered_in_both_languages()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        var persian = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id));
        var english = WebUtility.HtmlDecode(await MarkupAsync(harness, tenant.Id, culture: "en"));

        persian.Should().Contain("data-text-on=\"روی این دستگاه روشن است.\"");
        english.Should().Contain("data-text-on=\"On for this device.\"");
    }

    // ── subscribing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A device registers against the person who is signed in, and against nothing the caller sent.</b>
    ///
    /// <para>The tenant and the user come off the principal. A caller who could name the workspace
    /// their device belongs to could subscribe to somebody else's notifications.</para>
    /// </summary>
    [Fact]
    public async Task Subscribing_records_the_device_against_the_signed_in_person()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TokenAsync(client);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        using var response = await PostAsync(
            client,
            token,
            "/api/notifications/subscribe",
            new { endpoint = "https://push.example.test/abc", p256dh, auth });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = harness.NewDbContext();
        var device = await db.PushSubscriptions.SingleAsync();

        device.UserId.Should().Be(NotificationsPageHarness.UserId);
        device.TenantId.Should().Be(tenant.Id);
        device.Culture.Should().Be("fa", "the device's language is the panel's at the moment it subscribed");
    }

    /// <summary>
    /// An operator's device carries no workspace, because an operator has none.
    ///
    /// <para>Null rather than <c>Guid.Empty</c>: an empty id in a scoped read matches nothing, which
    /// would be an operator who is never notified with nothing anywhere saying why.</para>
    /// </summary>
    [Fact]
    public async Task An_operators_device_carries_no_workspace()
    {
        await using var harness = new NotificationsPageHarness();
        harness.SeedWorkspace();

        using var client = harness.NewClient(tenantId: null, asOperator: true, keepCookies: true);
        var token = await TokenAsync(client, asOperator: true);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        using var response = await PostAsync(
            client,
            token,
            "/api/notifications/subscribe",
            new { endpoint = "https://push.example.test/operator", p256dh, auth });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = harness.NewDbContext();
        (await db.PushSubscriptions.SingleAsync()).TenantId.Should().BeNull();
    }

    /// <summary>
    /// <b>A deployment with no keys refuses to store a subscription at all.</b>
    ///
    /// <para>Rather than storing a row that answers 403 for ever. The page unsubscribes the browser
    /// again when it sees this, so the reader is not left with a control that says it is on.</para>
    /// </summary>
    [Fact]
    public async Task An_unconfigured_deployment_refuses_to_store_a_subscription()
    {
        await using var harness = new NotificationsPageHarness(configured: false);
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);

        // The page draws no control, so there is no token in it. Taken from another screen that
        // wears the same shell — the endpoint has to be reachable for this refusal to be the one
        // under test rather than a 400.
        var token = await TokenAsync(client, path: "/files");
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        using var response = await PostAsync(
            client,
            token,
            "/api/notifications/subscribe",
            new { endpoint = "https://push.example.test/abc", p256dh, auth });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var db = harness.NewDbContext();
        (await db.PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>Keys that are not a device's are refused before a row exists.</summary>
    [Fact]
    public async Task A_subscription_that_is_not_the_shape_a_browser_produces_is_refused()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TokenAsync(client);

        using var response = await PostAsync(
            client,
            token,
            "/api/notifications/subscribe",
            new { endpoint = "https://push.example.test/abc", p256dh = "nope", auth = "nope" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// <b>The two writes are behind the antiforgery token.</b>
    ///
    /// <para>They are called from a fetch and are cookie-authenticated, which is exactly the shape a
    /// cross-site POST attacks. Without the token, any page on the internet could register its own
    /// endpoint against a signed-in reader's account — and would then receive that reader's
    /// notifications.</para>
    /// </summary>
    [Theory]
    [InlineData("/api/notifications/subscribe")]
    [InlineData("/api/notifications/unsubscribe")]
    public async Task A_write_without_the_antiforgery_token_is_refused(string path)
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        await TokenAsync(client);

        using var response = await client.PostAsJsonAsync(
            new Uri(path, UriKind.Relative),
            new { endpoint = "https://push.example.test/abc", p256dh = "x", auth = "y" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── unsubscribing ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unsubscribing forgets the device, and says nothing about whether there was one.
    ///
    /// <para>204 either way: the page calls this after the browser has already given its
    /// subscription up, so «there was nothing to remove» is a success — and a 404 here would confirm
    /// whether an endpoint somebody named is registered.</para>
    /// </summary>
    [Fact]
    public async Task Unsubscribing_forgets_the_device_and_answers_the_same_either_way()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TokenAsync(client);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        const string endpoint = "https://push.example.test/abc";

        using (var subscribed = await PostAsync(
            client, token, "/api/notifications/subscribe", new { endpoint, p256dh, auth }))
        {
            subscribed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var first = await PostAsync(
            client, token, "/api/notifications/unsubscribe", new { endpoint }))
        {
            first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var again = await PostAsync(
            client, token, "/api/notifications/unsubscribe", new { endpoint }))
        {
            again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        await using var db = harness.NewDbContext();
        (await db.PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The screen counts this person's devices and lists none of them.
    ///
    /// <para>A count is enough for «is this on». A device name, a browser or an address would each be
    /// a record of where somebody was, kept on a server to no end — the row carries none of them for
    /// that reason.</para>
    /// </summary>
    [Fact]
    public async Task The_screen_counts_the_devices_and_names_none_of_them()
    {
        await using var harness = new NotificationsPageHarness();
        var tenant = harness.SeedWorkspace();

        using var client = harness.NewClient(tenant.Id, keepCookies: true);
        var token = await TokenAsync(client);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        const string endpoint = "https://push.example.test/first-device";

        using (await PostAsync(client, token, "/api/notifications/subscribe", new { endpoint, p256dh, auth }))
        {
            // Registered. What the screen does with it is the assertion below.
        }

        var markup = await client.GetStringAsync(new Uri("/notifications", UriKind.Relative));

        var text = WebUtility.HtmlDecode(markup);

        // «۱ دستگاه اعلان می‌گیرد» — asserted in two pieces because UiText.Ltr wraps the figure in
        // U+2066…U+2069, which is invisible and sits between the numeral and the noun. A Persian
        // paragraph would otherwise lay the number out on the wrong side of its own unit.
        text.Should().Contain("دستگاه اعلان می‌گیرد");
        text.Should().Contain("۱", "one device, in the panel's own numerals");
        markup.Should().NotContain(endpoint, "an endpoint is a bearer string for writing to a phone");
        markup.Should().NotContain(p256dh);
    }

    // ── reading the screen ──────────────────────────────────────────────────────────────────────

    private static async Task<string> MarkupAsync(
        NotificationsPageHarness harness,
        Guid? tenantId,
        bool asOperator = false,
        string culture = "fa")
    {
        using var client = harness.NewClient(tenantId, asOperator);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/notifications");
        request.Headers.Add("Cookie", $".AspNetCore.Culture=c%3D{culture}%7Cuic%3D{culture}");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The antiforgery token, read the way the page's own script reads it.</summary>
    private static async Task<string> TokenAsync(
        HttpClient client,
        bool asOperator = false,
        string path = "/notifications")
    {
        var markup = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        var attribute = Regex.Match(
            markup,
            """data-antiforgery-token="(?<token>[^"]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        if (attribute.Success) return attribute.Groups["token"].Value;

        // Every panel page carries a form with the hidden field in it — the language switch is in
        // the shell — so this is the fallback for a screen that draws no mount point of its own.
        var hidden = Regex.Match(
            markup,
            """name="__RequestVerificationToken"[^>]*?value="(?<token>[^"]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        hidden.Success.Should().BeTrue($"{path} rendered no antiforgery token (operator: {asOperator})");

        return hidden.Groups["token"].Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string token,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("RequestVerificationToken", token);

        return await client.SendAsync(request);
    }
}
