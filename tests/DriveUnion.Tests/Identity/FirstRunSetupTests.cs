using System.Net;
using DriveUnion.Infrastructure.Seeding;
using FluentAssertions;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// The first screen, over the real pipeline.
///
/// A product whose first screen requires a command line has no first screen: before this, an empty
/// database could only be opened by setting <c>DriveUnion:Seed:OperatorPassword</c> in user-secrets
/// and restarting. What is proved here is the pair of behaviours that makes the second door safe —
/// that it is there when there is nobody, and that it is *gone* the moment there is somebody, on
/// the POST as well as on the GET.
/// </summary>
public class FirstRunSetupTests
{
    private const string Setup = "/Identity/Account/Setup";
    private const string Login = "/Identity/Account/Login";

    private const string OperatorEmail = "owner@driveunion.test";

    // Fixture values. The real password is typed by the person at the screen and is never in a file.
    private const string GoodPassword = "F1rst!Operator";
    private const string WeakPassword = "tinypw";

    [Fact]
    public async Task With_no_operator_the_panel_leads_to_the_first_run_screen()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        // Any panel page. The challenge is the cookie handler's, and its LoginPath is the address
        // the whole product treats as the way in.
        using var challenged = await client.GetAsync(new Uri("/links", UriKind.Relative));

        challenged.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The cookie handler writes an absolute location with the return url on it, so the path is
        // what is worth asserting.
        challenged.Headers.Location!.AbsolutePath.Should().Be(Login);

        using var wayIn = await client.GetAsync(new Uri(Login, UriKind.Relative));

        wayIn.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await wayIn.Content.ReadAsStringAsync();

        // The setup form, not the sign-in form: an empty database has no credential that could pass
        // the latter, and offering it is a locked door with no key cut for it.
        html.Should().Contain($"action=\"{Setup}\"");
        html.Should().Contain("name=\"ConfirmPassword\"");
    }

    [Fact]
    public async Task The_screen_is_also_at_its_own_address_and_says_what_is_required()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(Setup, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // Before the operator submits, and read out of IdentityOptions rather than transcribed —
        // ۱۰ is Program.cs's RequiredLength, in the Persian digits the shell writes prose in.
        html.Should().Contain("دست‌کم ۱۰ نویسه");
        html.Should().Contain("دست‌کم یک رقم");

        // The panel's own shell, not a second one.
        html.Should().Contain("brand-mark");
    }

    [Fact]
    public async Task The_operator_it_creates_is_signed_in_and_reaches_the_accounts_screen()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient(keepCookies: true);

        var token = await IdentityPagesHarness.AntiforgeryTokenAsync(client, Setup);

        using var created = await client.PostAsync(
            new Uri(Setup, UriKind.Relative),
            Form(token, OperatorEmail, GoodPassword));

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        created.Headers.Location!.OriginalString.Should().Be("/accounts");

        var user = harness.AllUsers().Should().ContainSingle().Subject;

        user.Email.Should().Be(OperatorEmail);
        user.IsOperator.Should().BeTrue();
        user.TenantId.Should().BeNull();
        user.Id.Should().Be(FirstOperator.SlotId);

        // /accounts is guarded by DriveUnionPolicies.Operator and nothing in this harness forges a
        // principal — so reaching it is the operator claim being on the cookie the POST just set.
        using var accounts = await client.GetAsync(new Uri("/accounts", UriKind.Relative));

        accounts.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The one sign-in in the panel that is deliberately not persistent, pinned so that «not» stays
    /// a decision rather than the line somebody forgot when the sign-in form got its default.
    ///
    /// <para>Everywhere else the answer to "stay signed in?" is yes, because the form asks it, ticks
    /// it and says what it means. This screen has two password boxes and a button and asks nothing —
    /// so a persistent cookie here would mint the longest-lived credential in the product, for the
    /// account that owns every Google account and every workspace, without the person ever being
    /// offered the choice. The cost of the other answer is one sign-in on the form that does offer
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task The_operator_the_setup_screen_creates_is_not_kept_signed_in()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient(keepCookies: true);

        var token = await IdentityPagesHarness.AntiforgeryTokenAsync(client, Setup);

        using var created = await client.PostAsync(
            new Uri(Setup, UriKind.Relative),
            Form(token, OperatorEmail, GoodPassword));

        created.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var cookie = harness.PanelCookieOn(created);

        cookie.Should().NotBeNull("the operator is signed in by this request");

        // No expiry: a session cookie, gone when the browser is closed. ExpireTimeSpan still bounds
        // the ticket, so this is shorter than an ordinary sign-in and never longer.
        cookie!.Expires.Should().BeNull();
        cookie.MaxAge.Should().BeNull();
    }

    [Fact]
    public async Task Once_an_operator_exists_the_route_is_gone_on_get_and_on_post()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient(keepCookies: true);

        // Taken while the door is still open. This is the saved page, the second tab and the
        // hand-written request — all of which arrive carrying a token that was valid when issued,
        // so the antiforgery filter passes them straight through to the action.
        var token = await IdentityPagesHarness.AntiforgeryTokenAsync(client, Setup);

        harness.SeedOperator();

        using var get = await client.GetAsync(new Uri(Setup, UriKind.Relative));

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var post = await client.PostAsync(
            new Uri(Setup, UriKind.Relative),
            Form(token, "second@driveunion.test", GoodPassword));

        // Refused server-side on the request that would have written, not hidden by the view that
        // did not draw the form.
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);

        harness.AllUsers().Should().ContainSingle().Which.IsOperator.Should().BeTrue();
    }

    [Fact]
    public async Task Once_an_operator_exists_the_way_in_is_the_sign_in_form_again()
    {
        using var harness = new IdentityPagesHarness();
        harness.SeedOperator();

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri(Login, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain($"action=\"{Login}\"");
        html.Should().NotContain("name=\"ConfirmPassword\"");
    }

    [Fact]
    public async Task A_password_the_policy_refuses_is_answered_in_identitys_own_words()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient(keepCookies: true);

        var token = await IdentityPagesHarness.AntiforgeryTokenAsync(client, Setup);

        using var refused = await client.PostAsync(
            new Uri(Setup, UriKind.Relative),
            Form(token, OperatorEmail, WeakPassword));

        refused.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await refused.Content.ReadAsStringAsync();

        // The rule that was broken, named to the person who broke it. RequiredLength = 10 lives in
        // Program.cs; Identity writes the sentence and DriveUnionIdentityErrorDescriber translates
        // it, which is why this asserts on the Persian now — that describer is the only reason a
        // refusal is not the one patch of English left inside a Persian page.
        //
        // Decoded first: Razor writes Persian as numeric character references, so asserting on the
        // raw HTML would pass against a page that never said this at all.
        var text = System.Net.WebUtility.HtmlDecode(html);
        text.Should().Contain("۱۰");

        // Never rendered back into the page: a refused password in the HTML is a password in the
        // browser's back-forward cache and in anything that keeps a response body.
        html.Should().NotContain(WeakPassword);

        harness.AllUsers().Should().BeEmpty();

        // And the door is still open — a refusal must not close the only way in.
        using var again = await client.GetAsync(new Uri(Setup, UriKind.Relative));

        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_different_passwords_are_refused_before_anything_is_created()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient(keepCookies: true);

        var token = await IdentityPagesHarness.AntiforgeryTokenAsync(client, Setup);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Email"] = OperatorEmail,
            ["Password"] = GoodPassword,
            ["ConfirmPassword"] = GoodPassword + "x",
        });

        using var refused = await client.PostAsync(new Uri(Setup, UriKind.Relative), form);

        refused.StatusCode.Should().Be(HttpStatusCode.OK);

        // There is no password reset in M1, so a typo here would lock the owner out of their own
        // panel with nothing but the database to fix it.
        harness.AllUsers().Should().BeEmpty();
    }

    [Fact]
    public async Task The_generated_password_offer_is_absent_outside_development()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(Setup, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        // Absent, not hidden: a production panel invents no credentials, so neither the box nor the
        // script that fills it is on the page at all.
        html.Should().NotContain("data-password-suggestion");
        html.Should().NotContain("getRandomValues");
    }

    [Fact]
    public async Task In_development_the_browser_offers_one_and_it_has_to_be_accepted()
    {
        using var harness = new IdentityPagesHarness("Development");
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(Setup, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("data-password-suggestion");

        // Generated in the operator's own browser from its CSPRNG. A server-side one has been held
        // by the server: in a response body, in whatever logs it, and in a process nobody owns.
        html.Should().Contain("getRandomValues");

        // A suggestion, not a default — there is an explicit control that puts it in the boxes, and
        // the boxes themselves ship empty.
        html.Should().Contain("data-suggestion-use");
        html.Should().NotContain("name=\"Password\" type=\"password\" value=");
    }

    private static FormUrlEncodedContent Form(string token, string email, string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Email"] = email,
            ["Password"] = password,
            ["ConfirmPassword"] = password,
        });
}
