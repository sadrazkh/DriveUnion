using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The consent flow walked over HTTP, through the real pipeline, up to the edge of Google and back
/// from it with an error. Nothing here reaches Google — the redirect is read, never followed.
/// </summary>
public class GoogleConnectOverHttpTests
{
    private const string GoogleAuthorizeHost = "accounts.google.com";

    [Fact]
    public async Task The_hidden_field_the_accounts_page_renders_is_the_one_the_action_binds()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/accounts/connect", Form(token, popup: "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Host.Should().Be(GoogleAuthorizeHost);

        // "pop." only appears if the field's name reached the parameter. A name that drifted by one
        // character would take the popup away with every other test in this lane still passing.
        OperatorPanelHarness.IssuedState(response).Should().StartWith("pop.");
    }

    [Fact]
    public async Task The_same_form_with_the_field_untouched_is_the_flow_that_has_always_worked()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/accounts/connect", Form(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        OperatorPanelHarness.IssuedState(response).Should().StartWith("top.");
    }

    /// <summary>
    /// The popup is a window, not a new endpoint. Nothing about it was allowed to make this POST
    /// forgeable.
    /// </summary>
    [Fact]
    public async Task Connect_without_an_antiforgery_token_is_still_refused()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        using var response = await client.PostAsync(
            "/accounts/connect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["popup"] = "true" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OperatorPanelHarness.IssuedState(response).Should().BeNull();
    }

    [Fact]
    public async Task A_flow_that_started_in_a_popup_comes_back_to_a_page_that_reports_and_closes()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var started = await client.PostAsync("/accounts/connect", Form(token, popup: "true"));
        var state = OperatorPanelHarness.IssuedState(started)!;

        // The cookie jar carries the state cookie back, exactly as the browser would on Google's
        // top-level redirect. access_denied because a real code cannot be exchanged from here.
        using var response = await client.GetAsync(
            $"/accounts/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Decoded because Razor's default encoder writes every Persian letter as a numeric entity —
        // correct on the wire, unreadable in an assertion.
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        html.Should().Contain("اتصال اکانت لغو شد.", "the operator has to be told why, where they are looking");
        html.Should().Contain("postMessage");

        // An explicit target origin, and the one value it must never be.
        html.Should().Contain("window.location.origin");
        html.Should().NotContain("'*'", "a wildcard target origin publishes the outcome to any page");
    }

    [Fact]
    public async Task A_flow_that_started_in_the_panel_comes_back_to_a_redirect()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var started = await client.PostAsync("/accounts/connect", Form(token, popup: "false"));
        var state = OperatorPanelHarness.IssuedState(started)!;

        using var response = await client.GetAsync(
            $"/accounts/callback?error=access_denied&state={Uri.EscapeDataString(state)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/accounts");
    }

    /// <summary>
    /// A callback nobody was sent, claiming to be a popup. Without the cookie there is no popup, and
    /// the answer is the ordinary redirect — a stranger's link cannot make the panel render a page
    /// that talks to <c>window.opener</c>.
    /// </summary>
    [Fact]
    public async Task A_state_from_the_query_alone_gets_no_closing_page()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(
            "/accounts/callback?code=4/forged&state=pop.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>
    /// This machine, and every deployment before its first account: the screen an operator opens to
    /// find out that nothing is connected has to render.
    /// </summary>
    [Fact]
    public async Task The_accounts_page_renders_with_no_Google_credentials_at_all()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var response = await client.GetAsync("/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // The screen names the three settings rather than just refusing.
        html.Should().Contain("Google:ClientId")
            .And.Contain("Google:ClientSecret")
            .And.Contain("Google:RedirectUri");
    }

    private static FormUrlEncodedContent Form(string token, string popup) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["popup"] = popup,
        });
}
