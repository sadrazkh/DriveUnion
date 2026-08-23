using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The primary action on «اکانت‌های گوگل», in both of its states.
///
/// The screen used to render «+ افزودن اکانت با OAuth» with <c>disabled</c> whenever no OAuth client
/// was configured, and put the explanation several screens below it. Nothing was broken — Google
/// will not take a request without a client id, and only the owner can make one — but a primary
/// action that refuses to depress and offers nothing instead reads as a broken product. These tests
/// hold the two halves of the fix apart: the control is now always live and always leads somewhere,
/// and none of that made connecting one step easier for anybody without real credentials.
///
/// Nothing here reaches Google. The harness's IDriveClient throws on contact, and every request
/// below either stops at the redirect to the consent screen or never gets that far.
/// </summary>
public class GoogleConnectCallToActionTests
{
    /// <summary>The fragment the unconfigured screen's primary action points at.</summary>
    private const string SetupPanelId = "google-setup";

    /// <summary>
    /// The whole point. A control the operator cannot press, on the screen whose only job is to get
    /// them started, with nothing offered instead — that is what was removed, and this is what stops
    /// it coming back.
    /// </summary>
    [Fact]
    public async Task The_unconfigured_screen_has_no_dead_control()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var response = await client.GetAsync("/accounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = MainContent(await response.Content.ReadAsStringAsync());

        // Scoped to the page's own content on purpose: the shell marks the two navigation items
        // whose milestones are unwritten with aria-disabled, and that is a different question.
        content.Should().NotContain("disabled", "a primary action nobody can press is the defect this screen had");
    }

    /// <summary>
    /// Pressable is only half of it — pressing has to arrive somewhere. Both ends of the route are
    /// asserted here, because either one alone is a link that appears to do nothing.
    /// </summary>
    [Fact]
    public async Task The_primary_action_is_a_route_to_the_setup_form_without_any_script()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var content = MainContent(await client.GetStringAsync("/accounts"));

        content.Should().Contain($"href=\"#{SetupPanelId}\"", "a plain anchor is the no-JavaScript answer");

        var (attributes, body) = SetupPanel(content);

        // Unfolded, or the fragment lands the operator on a closed row and the route ends there.
        attributes.Should().Contain("open");

        body.Should().Contain("id=\"setup-client-id\"", "the field they were sent to fill");
        body.Should().Contain("data-setup-focus", "the enhancement focuses it by name, not by position");
        body.Should().Contain("/accounts/google-credentials", "and the form that field belongs to");
    }

    /// <summary>
    /// The reason sits with the control. Someone who has just read a button that will not connect
    /// them has to meet the explanation before anything else, not below the panel it is about.
    /// </summary>
    [Fact]
    public async Task The_reason_reaches_the_reader_before_the_setup_panel_does()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var content = MainContent(await client.GetStringAsync("/accounts"));

        // The part no amount of engineering removes, said where the refusal happens.
        content.Should().Contain("پروژه‌ی Google Cloud خودِ شما", "the client can only be made in the owner's own project");
        content.Should().Contain("این پنل نمی‌تواند آن را بسازد", "written as a fact the reader needs, not as an apology");

        // Reading order, top to bottom: where you are, what you can do and why it is not a
        // connection yet, how to make it one, and where the result will appear.
        var title = content.IndexOf("page-title", StringComparison.Ordinal);
        var action = content.IndexOf("data-setup-jump", StringComparison.Ordinal);
        // The class and not the id: the id is also the anchor's aria-describedby, which is the
        // control naming its own explanation and therefore sits earlier than the paragraph itself.
        var reason = content.IndexOf("class=\"accounts-cta-why\"", StringComparison.Ordinal);
        var panel = content.IndexOf($"id=\"{SetupPanelId}\"", StringComparison.Ordinal);
        var accounts = content.IndexOf("هنوز اکانتی متصل نیست", StringComparison.Ordinal);

        title.Should().BeGreaterThan(-1, "the screen still names itself");
        action.Should().BeGreaterThan(title);
        reason.Should().BeGreaterThan(action, "the explanation belongs under the control, not under the panel");
        panel.Should().BeGreaterThan(reason);
        accounts.Should().BeGreaterThan(panel, "the empty list is the consequence, so it reads last");
    }

    /// <summary>
    /// The three settings are still named. A deployment that does have a terminal can supply them
    /// from the environment instead of the form, and it outranks the form when it does. They moved
    /// from a red paragraph at the foot of the screen into the panel that is about them, and this is
    /// what says they moved rather than went.
    /// </summary>
    [Fact]
    public async Task The_settings_that_can_supply_the_client_are_named_inside_the_panel()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var (_, body) = SetupPanel(MainContent(await client.GetStringAsync("/accounts")));

        body.Should().Contain("Google:ClientId")
            .And.Contain("Google:ClientSecret")
            .And.Contain("Google:RedirectUri");
    }

    /// <summary>
    /// The line this change was not allowed to cross. The button being pressable must not mean a
    /// request can reach Google without a client id: <c>Connect</c> refuses exactly as it did, and
    /// the harness's IDriveClient throws on contact — so a call that leaked out would fail loudly
    /// rather than pass quietly.
    /// </summary>
    [Fact]
    public async Task Connect_while_unconfigured_is_still_refused_and_reaches_nothing_outside()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/accounts/connect", Connect(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = response.Headers.Location!.ToString();
        location.Should().Contain("/accounts");
        location.Should().NotContain("google.com", "no authorization request may leave without a client id");

        // No consent flow was started, so there is no nonce to come back with.
        OperatorPanelHarness.IssuedState(response).Should().BeNull();

        // And the refusal is said on the screen the operator lands back on.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().Contain("پیکربندی OAuth گوگل کامل نیست");
    }

    /// <summary>
    /// The same refusal in the window the operator is looking at, which is where they are looking
    /// when the popup enhancement is running.
    /// </summary>
    [Fact]
    public async Task Connect_while_unconfigured_refuses_inside_the_popup_too()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/accounts/connect", Connect(token, popup: "true"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        OperatorPanelHarness.IssuedState(response).Should().BeNull();

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().Contain("پیکربندی گوگل کامل نیست");
    }

    /// <summary>
    /// Nothing about making the button pressable was allowed to make this POST forgeable.
    /// </summary>
    [Fact]
    public async Task Connect_while_unconfigured_still_needs_the_antiforgery_token()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var response = await client.PostAsync(
            "/accounts/connect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["popup"] = "true" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OperatorPanelHarness.IssuedState(response).Should().BeNull();
    }

    /// <summary>
    /// With a client configured the screen is the one it has always been: the same label, the same
    /// form, the same hidden field, the same redirect to Google — and no route to a setup panel,
    /// because the panel folds away once there is nothing left to set up.
    /// </summary>
    [Fact]
    public async Task The_configured_screen_still_posts_the_connect_form()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var content = MainContent(await client.GetStringAsync("/accounts"));

        content.Should().Contain("action=\"/accounts/connect\"");
        content.Should().Contain("data-google-connect>", "the enhancement finds the form by this attribute");
        content.Should().Contain("name=\"popup\"");
        content.Should().Contain("data-google-connect-status", "the popup still reports into this");
        content.Should().Contain("+ افزودن اکانت با OAuth");
        content.Should().NotContain("data-setup-jump", "there is nothing left to be sent to set up");

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/accounts/connect", Connect(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Host.Should().Be("accounts.google.com");
        OperatorPanelHarness.IssuedState(response).Should().StartWith("top.");
    }

    /// <summary>
    /// The screen is still operator-only, in both of its states. A customer who types the address
    /// gets a 403 rather than a page explaining how the operator's Google project is put together.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_customer_gets_403_from_the_screen_and_from_connect(bool googleConfigured)
    {
        using var harness = new OperatorPanelHarness(googleConfigured, isOperator: false);
        using var client = harness.NewClient();

        using var page = await client.GetAsync("/accounts");
        page.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Refused before any filter runs, so there is no token to fetch and none to send — which is
        // the shape of the request an attacker would actually make.
        using var connect = await client.PostAsync(
            "/accounts/connect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["popup"] = "false" }));

        connect.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        OperatorPanelHarness.IssuedState(connect).Should().BeNull();
    }

    private static FormUrlEncodedContent Connect(string token, string popup) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["popup"] = popup,
        });

    /// <summary>
    /// The page's own markup, without the shell around it, and decoded.
    ///
    /// Scoped because the layout renders navigation items for milestones that have no controller
    /// yet and marks them <c>aria-disabled</c>: an assertion about dead controls on this screen has
    /// to be able to tell the two apart. Decoded because Razor's encoder writes every Persian letter
    /// as a numeric entity — correct on the wire, unreadable in an assertion.
    /// </summary>
    private static string MainContent(string html)
    {
        var match = Regex.Match(
            html,
            "<main class=\"app-content\">(.*)</main>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The page rendered no <main class=\"app-content\"> region.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>The setup panel's opening tag and its contents, so an assertion can name which.</summary>
    private static (string Attributes, string Body) SetupPanel(string content)
    {
        var match = Regex.Match(
            content,
            $"<details([^>]*id=\"{SetupPanelId}\"[^>]*)>(.*?)</details>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"The accounts page rendered no <details id=\"{SetupPanelId}\">.");

        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
