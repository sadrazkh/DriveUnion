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
    public async Task A_saved_secret_is_encrypted_in_the_database_and_never_comes_back_down_the_wire()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        using var saved = await OperatorPanelHarness.SaveCredentialsAsync(
            client,
            TypedClientId,
            TypedSecret,
            OperatorPanelHarness.OriginRedirectUri);

        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // A row, not a file. The file this replaced lived inside the container and a redeploy
        // deleted it, taking the whole pool down with it — the refresh tokens survived and could no
        // longer be refreshed.
        var row = harness.StoredClients().Should().ContainSingle().Subject;
        row.ClientId.Should().Be(TypedClientId);
        row.ClientSecretProtected.Should().NotBeNull();
        row.ClientSecretProtected.Should().NotContain(
            TypedSecret,
            "the secret is protected at rest by the token protector");

        // Every response the operator's browser can get its hands on, checked for the secret.
        foreach (var path in new[] { "/accounts", "/accounts/google-credentials" })
        {
            using var page = path == "/accounts"
                ? await client.GetAsync(path)
                : await OperatorPanelHarness.SaveCredentialsAsync(
                    client,
                    TypedClientId,
                    clientSecret: null,
                    OperatorPanelHarness.OriginRedirectUri,
                    row.Id);

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
        var seeded = harness.SeedClient("already-there.apps.googleusercontent.com");

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

        // The two routes that reach a client already in the table. Removing one strands every
        // account it refreshes, and promoting one decides which client the next account is bound to.
        using var remove = await client.PostAsync(
            $"/accounts/google-credentials/{seeded.Id}/remove",
            EmptyForm());
        remove.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var use = await client.PostAsync(
            $"/accounts/google-credentials/{seeded.Id}/use",
            EmptyForm());
        use.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        harness.StoredClients().Should().ContainSingle("nothing a customer sent may have changed a row");
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
        harness.StoredClients().Should().BeEmpty();
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

        var id = harness.StoredClients().Should().ContainSingle().Subject.Id;

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);
        using var cleared = await client.PostAsync(
            $"/accounts/google-credentials/{id}/remove",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        cleared.StatusCode.Should().Be(HttpStatusCode.Redirect);
        harness.StoredClients().Should().BeEmpty();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().NotContain(TypedClientId);
    }

    /// <summary>
    /// The refusal, end to end and in a sentence. A refresh token can only be presented by the
    /// client that issued it, so removing a client accounts still name does not fail when it is
    /// pressed — it fails an hour later, on every one of them at once, as uploads reporting that
    /// storage is unavailable. Which is how this product lost its pool once already.
    /// </summary>
    [Fact]
    public async Task A_client_an_account_still_needs_cannot_be_removed()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        var seeded = harness.SeedClient(TypedClientId);
        harness.SeedAccount("pool-a1@example.com", "A1", TypedClientId);

        using var client = harness.NewClient();
        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            $"/accounts/google-credentials/{seeded.Id}/remove",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        harness.StoredClients().Should().ContainSingle();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));
        html.Should().Contain("این کلاینت حذف نشد");
        html.Should().Contain("A1", "the refusal names the account standing in the way");
    }

    /// <summary>
    /// Status said «قطع شده» and nothing said why, so a pool that died because a redeploy deleted
    /// its OAuth client read exactly like one whose consent screen had expired — and both read like
    /// nothing. The customer still gets the generic sentence; the operator gets Google's own words.
    /// </summary>
    [Fact]
    public async Task An_account_card_says_which_client_connected_it_and_why_it_last_failed()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        harness.SeedClient(TypedClientId, "C1");
        harness.SeedAccount(
            "pool-a1@example.com",
            "A1",
            TypedClientId,
            "Google rejected the grant (invalid_grant: Token has been expired or revoked).");

        using var client = harness.NewClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));

        html.Should().Contain("کلاینت C1");
        html.Should().Contain("Token has been expired or revoked");
        html.Should().Contain("آخرین خطا");
    }

    /// <summary>
    /// The card an operator would have needed on the morning the pool went dark: the account names a
    /// client that is not stored any more, so nothing can refresh it, and the screen says exactly
    /// that instead of showing a status badge with no explanation.
    /// </summary>
    [Fact]
    public async Task An_account_whose_client_is_gone_is_called_out_on_its_card()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        harness.SeedAccount("pool-a1@example.com", "A1", "deleted-last-deploy.apps.googleusercontent.com");

        using var client = harness.NewClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));

        html.Should().Contain("کلاینتی که این اکانت با آن وصل شده دیگر ذخیره نیست");
        html.Should().Contain("deleted-last-deploy.apps.googleusercontent.com");
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
