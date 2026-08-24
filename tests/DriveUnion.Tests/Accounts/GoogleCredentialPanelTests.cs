using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Infrastructure.Google;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The credentials screen over HTTP, through the real pipeline, with a real file behind it.
///
/// The unit tests beside this one settle the precedence rule. This settles the four things a
/// controller call cannot show: that the screen an operator meets with nothing configured actually
/// teaches them what to do, that the redirect URI it prints is the one the authorization request
/// really uses, that a secret saved through the form never comes back down the wire, and that none
/// of it is reachable without the operator claim.
///
/// Nothing here reaches Google. Every test stops at the redirect to the consent screen and reads it.
/// </summary>
public class GoogleCredentialPanelTests
{
    private const string TypedClientId = "typed-in-the-panel.apps.googleusercontent.com";
    private const string TypedSecret = "GOCSPX-never-render-this-anywhere";

    /// <summary>
    /// The screen a new operator opens first. It is written as instructions, not as an error,
    /// because the thing it is reporting — «no Google project yet» — is the normal state of a fresh
    /// deployment rather than a fault.
    /// </summary>
    [Fact]
    public async Task The_unconfigured_screen_says_what_to_do_in_Google_Cloud()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var response = await client.GetAsync("/accounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        html.Should().Contain("Google Cloud Console", "the operator has to be told where to go");
        html.Should().Contain("Google Drive API", "an OAuth client without the API enabled fails at the first call");
        html.Should().Contain("Web application", "the wrong client type is refused by Google, not by us");

        // The seven-day trap from the design's §2 — the single most likely way this owner's first
        // connection breaks a week after it worked.
        html.Should().Contain("In production");
        html.Should().Contain("Testing");
        html.Should().Contain("هفت روز");

        // The restricted scope, named, so the "unverified app" warning is expected rather than
        // alarming when the operator meets it at Google.
        html.Should().Contain("https://www.googleapis.com/auth/drive");

        // And the redirect URI, rendered from this panel's own origin rather than as a placeholder
        // the operator has to assemble.
        html.Should().Contain(OperatorPanelHarness.OriginRedirectUri);

        // The three configuration keys are still named: a deployment that does have a terminal
        // should know the environment is an option and that it wins.
        html.Should().Contain("Google:ClientId")
            .And.Contain("Google:ClientSecret")
            .And.Contain("Google:RedirectUri");
    }

    /// <summary>
    /// The classic hour-long debugging session, pinned: Google compares <c>redirect_uri</c> as a
    /// string and says nothing useful when it differs, so what the screen tells the operator to
    /// register has to be exactly what the authorization request carries.
    /// </summary>
    [Fact]
    public async Task The_redirect_uri_the_screen_shows_is_the_one_the_authorization_url_uses()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var shown = EffectiveRedirectUri(await client.GetStringAsync("/accounts"));

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/accounts/connect", Connect(token));

        var sent = QueryHelpers
            .ParseQuery(response.Headers.Location!.Query)["redirect_uri"]
            .ToString();

        sent.Should().Be(shown);
        sent.Should().Be(OperatorPanelHarness.RedirectUri, "configuration supplied it here");
    }

    /// <summary>
    /// The same pin from the other end: with nothing configured, the URI the screen offers for
    /// pasting into Google Cloud has to be the one that will actually be sent once it is saved.
    /// </summary>
    [Fact]
    public async Task The_suggested_redirect_uri_is_the_one_that_gets_used_once_it_is_saved()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var suggested = SuggestedRedirectUri(await client.GetStringAsync("/accounts"));
        suggested.Should().Be(OperatorPanelHarness.OriginRedirectUri);

        using var saved = await OperatorPanelHarness.SaveCredentialsAsync(
            client,
            TypedClientId,
            TypedSecret,
            suggested);

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // No restart between saving and connecting. This is the whole feature.
        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/accounts/connect", Connect(token));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Host.Should().Be("accounts.google.com");

        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        query["client_id"].ToString().Should().Be(TypedClientId);
        query["redirect_uri"].ToString().Should().Be(suggested);

        // The consent flow the popup work established is untouched by any of this — including both
        // prompt values, which a client typed into the panel has to carry exactly as one supplied
        // from the environment does. GoogleOAuthUrlsTests is where they are argued.
        query["access_type"].ToString().Should().Be("offline");
        query["prompt"].ToString().Should().Be(GoogleOAuthUrls.Prompt);
        OperatorPanelHarness.IssuedState(response).Should().StartWith("top.");
    }

    [Fact]
    public async Task A_saved_secret_is_encrypted_on_disk_and_never_comes_back_down_the_wire()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var saved = await OperatorPanelHarness.SaveCredentialsAsync(
            client,
            TypedClientId,
            TypedSecret,
            OperatorPanelHarness.OriginRedirectUri);

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var onDisk = harness.CredentialStoreText();
        onDisk.Should().NotBeNull();
        onDisk.Should().NotContain(TypedSecret, "the secret is protected at rest by the token protector");

        // Every response the operator's browser can get its hands on, checked for the secret.
        foreach (var path in new[] { "/accounts", "/accounts/google-credentials" })
        {
            using var page = path == "/accounts"
                ? await client.GetAsync(path)
                : await OperatorPanelHarness.SaveCredentialsAsync(
                    client,
                    TypedClientId,
                    clientSecret: null,
                    OperatorPanelHarness.OriginRedirectUri);

            var body = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
            body.Should().NotContain(TypedSecret, "a secret that can be rendered eventually is");
        }

        // It is still there and still usable — the screen says so without saying what it is.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().Contain("ذخیره شده");
    }

    /// <summary>
    /// A deployment that supplies <c>Google:ClientId</c> from the environment is not silently
    /// overridden by something typed into a form — and is told, on the screen, that it is not.
    /// </summary>
    [Fact]
    public async Task Configuration_outranks_the_form_end_to_end()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        using var saved = await OperatorPanelHarness.SaveCredentialsAsync(
            client,
            TypedClientId,
            TypedSecret,
            "https://ignored.example.test/accounts/callback");

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/accounts/connect", Connect(token));

        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        query["client_id"].ToString().Should().Be(OperatorPanelHarness.ClientId);
        query["redirect_uri"].ToString().Should().Be(OperatorPanelHarness.RedirectUri);

        // Not the one-shot TempData notice from the save — a standing sentence on the screen, so an
        // operator arriving tomorrow still learns why the client id they typed is not the one in use.
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().Contain("پیکربندی سرور همیشه اولویت دارد");
        html.Should().Contain(TypedClientId, "what the panel is holding is still shown, and still theirs");
    }

    /// <summary>
    /// The credentials that reach the operator's entire Drive pool. A customer who types the address
    /// gets a 403 — a hidden button is not an access control.
    /// </summary>
    [Fact]
    public async Task The_settings_surface_is_operator_only()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false, isOperator: false);
        using var client = harness.NewClient();

        using var page = await client.GetAsync("/accounts");
        page.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Refused by the authorisation middleware before any filter, so there is no token to fetch
        // and none to send — which is the shape of the request an attacker would actually make.
        using var save = await client.PostAsync(
            "/accounts/google-credentials",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ClientId"] = TypedClientId,
                ["ClientSecret"] = TypedSecret,
                ["RedirectUri"] = OperatorPanelHarness.OriginRedirectUri,
            }));

        save.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var clear = await client.PostAsync("/accounts/google-credentials/clear", EmptyForm());
        clear.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        harness.CredentialStoreText().Should().BeNull("nothing a customer sent may have been written");
    }

    /// <summary>
    /// Nothing about typing credentials into a page was allowed to make writing them forgeable.
    /// </summary>
    [Fact]
    public async Task Saving_without_an_antiforgery_token_is_refused()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var response = await client.PostAsync(
            "/accounts/google-credentials",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ClientId"] = TypedClientId,
                ["ClientSecret"] = TypedSecret,
                ["RedirectUri"] = OperatorPanelHarness.OriginRedirectUri,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        harness.CredentialStoreText().Should().BeNull();
    }

    /// <summary>
    /// The path the screen prints is a constant in the controller. This is what stops it drifting
    /// away from the route that actually answers it.
    /// </summary>
    [Fact]
    public async Task The_redirect_uri_the_screen_shows_is_a_real_endpoint()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var suggested = SuggestedRedirectUri(await client.GetStringAsync("/accounts"));

        using var response = await client.GetAsync(new Uri(suggested).AbsolutePath);

        // A callback with no state is refused, and that refusal is a redirect back to /accounts. A
        // 404 would mean the screen is telling the operator to register an address nothing serves.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task A_client_saved_and_then_removed_leaves_the_screen_where_it_started()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var saved = await OperatorPanelHarness.SaveCredentialsAsync(
            client,
            TypedClientId,
            TypedSecret,
            OperatorPanelHarness.OriginRedirectUri);
        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var cleared = await client.PostAsync(
            "/accounts/google-credentials/clear",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        cleared.StatusCode.Should().Be(HttpStatusCode.Redirect);
        harness.CredentialStoreText().Should().BeNull();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().NotContain(TypedClientId);
    }

    private static FormUrlEncodedContent EmptyForm() =>
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    private static FormUrlEncodedContent Connect(string token) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["popup"] = "false",
        });

    /// <summary>The redirect URI the screen reports as in force, read out of the markup.</summary>
    private static string EffectiveRedirectUri(string html) =>
        Scrape(html, "data-effective-redirect-uri");

    /// <summary>The redirect URI the screen offers for pasting into Google Cloud.</summary>
    private static string SuggestedRedirectUri(string html) => Scrape(html, "data-copy-value");

    private static string Scrape(string html, string attribute)
    {
        var match = Regex.Match(
            WebUtility.HtmlDecode(html),
            attribute + @"\s*>\s*([^<]+?)\s*<",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"The accounts page rendered no element carrying {attribute}.");

        return match.Groups[1].Value;
    }
}
