using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// The control a customer who cannot read Persian has to be able to find and use.
///
/// It is on the sign-in page on purpose — that is where somebody who cannot read the panel lands
/// first, and a language switch that only exists behind the sign-in form is a switch they never
/// reach. It is a form rather than a link because it writes a cookie that outlives the request, and
/// it is server-rendered rather than an island because the panel has to work with JavaScript off,
/// which is the entire reason the language is resolved on the server.
/// </summary>
public class CultureSwitchTests
{
    private const string OtherLanguageField = "name=\"culture\" value=\"([^\"]+)\"";

    [Fact]
    public async Task The_sign_in_page_carries_the_switch_and_it_needs_no_javascript()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync();

        // A real form with a real action, posted by a real submit button. Nothing here is wired up
        // by a bundle, and nothing here is an anchor that would change state on a GET.
        html.Should().Contain("<form method=\"post\" action=\"/Culture/Set\"");
        html.Should().Contain("name=\"returnUrl\" value=\"/Identity/Account/Login\"");
        html.Should().Contain("__RequestVerificationToken");

        // The label is the other language's own name, so it is recognisable to somebody who cannot
        // read the page it is on.
        html.Should().Contain("lang=\"en\"");
        html.Should().Contain(">English<");

        // And the offer is the language you are not reading.
        Regex.Match(html, OtherLanguageField, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Groups[1].Value.Should().Be("en");
    }

    [Fact]
    public async Task Pressing_it_stores_the_choice_and_comes_back_to_the_same_page()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        // A cookie jar, because what is under test is a cookie written by one response and read by
        // the next — and the antiforgery pair is the same conversation.
        using var client = harness.NewClient(keepCookies: true);

        string token;
        using (var before = await client.GetAsync(new Uri(LocalizationHarness.SignInPath, UriKind.Relative)))
        {
            var page = await LocalizationHarness.TextAsync(before);

            page.Should().Contain("<html dir=\"rtl\" lang=\"fa\"", "the visitor starts where everybody starts");
            token = LocalizationHarness.AntiforgeryToken(page);
        }

        using var switched = await client.PostAsync(
            new Uri(LocalizationHarness.SwitchPath, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["culture"] = "en",
                ["returnUrl"] = LocalizationHarness.SignInPath,
            }));

        switched.StatusCode.Should().Be(HttpStatusCode.Redirect);
        switched.Headers.Location!.OriginalString.Should().Be(LocalizationHarness.SignInPath);

        var setCookie = switched.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(LocalizationHarness.CultureCookieName, StringComparison.Ordinal));

        // Path=/ is load-bearing rather than tidy: it is what sends this cookie to /d/{slug} as
        // well, so the public download page can be taught to honour it without a second cookie.
        setCookie.Should().Contain("path=/");
        setCookie.Should().Contain("httponly", "nothing in the browser reads the language");

        // And the panel is English from here on, for this visitor, with nothing on the request
        // saying so but the cookie.
        using var after = await client.GetAsync(new Uri(LocalizationHarness.SignInPath, UriKind.Relative));
        var html = await LocalizationHarness.TextAsync(after);

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");

        // The switch now offers the way back, named in its own script.
        html.Should().Contain(">فارسی<");
        Regex.Match(html, OtherLanguageField, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Groups[1].Value.Should().Be("fa");
    }

    [Fact]
    public async Task A_language_the_panel_does_not_speak_is_refused_rather_than_stored()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        using var client = harness.NewClient(keepCookies: true);
        var token = await LocalizationHarness.TokenAsync(client);

        using var refused = await client.PostAsync(
            new Uri(LocalizationHarness.SwitchPath, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["culture"] = "de",
                ["returnUrl"] = LocalizationHarness.SignInPath,
            }));

        // Refused out loud. A cookie holding a tag nothing resolves would be discarded on every
        // request for a year, with the panel quietly ignoring a preference somebody set.
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task It_will_not_send_the_visitor_off_the_site()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        using var client = harness.NewClient(keepCookies: true);
        var token = await LocalizationHarness.TokenAsync(client);

        using var switched = await client.PostAsync(
            new Uri(LocalizationHarness.SwitchPath, UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["culture"] = "en",
                ["returnUrl"] = "https://phishing.example/panel",
            }));

        // Anonymous and reachable from the sign-in page, so an open redirect here is a phishing hop
        // wearing the product's own domain. The language is still changed; only the destination is.
        switched.StatusCode.Should().Be(HttpStatusCode.Redirect);
        switched.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task Ending_a_session_is_still_the_only_thing_the_shell_posts_to_identity()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync();

        // The switch is anonymous and the sign-in page renders it; the sign-out form must not have
        // followed it onto a page where it can only 401.
        html.Should().NotContain("/Identity/Account/Logout");
    }
}
