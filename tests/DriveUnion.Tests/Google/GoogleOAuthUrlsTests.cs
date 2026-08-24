using DriveUnion.Infrastructure.Google;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace DriveUnion.Tests.Google;

public class GoogleOAuthUrlsTests
{
    private static readonly GoogleOAuthOptions Configured = new()
    {
        ClientId = "client-id.apps.googleusercontent.com",
        ClientSecret = "secret",
        RedirectUri = "https://drive.example/oauth/google",
    };

    [Fact]
    public void The_consent_url_asks_for_the_refresh_token_the_pool_depends_on()
    {
        var query = Uri.UnescapeDataString(Query(state: "csrf-state"));

        // Without this, Google returns an access token and no refresh token — and the account stops
        // working an hour later, for reasons nothing in the panel can explain.
        query.Should().Contain("access_type=offline");

        query.Should().Contain("scope=https://www.googleapis.com/auth/drive");
        query.Should().Contain("redirect_uri=https://drive.example/oauth/google");
        query.Should().Contain("state=csrf-state");
        query.Should().Contain("response_type=code");
    }

    /// <summary>
    /// The bug this file exists to stop coming back.
    ///
    /// <c>prompt=consent</c> alone lets Google reuse whichever account the browser is already signed
    /// into, so the operator's second «افزودن اکانت» silently re-approved the first account and the
    /// pool never grew. <c>select_account</c> alone would fix that and take the refresh token away
    /// with it, which is a slower and much worse failure: the new account works for an hour and then
    /// cannot be renewed. Both, or neither is enough.
    /// </summary>
    [Fact]
    public void The_consent_url_asks_for_the_account_chooser_and_for_consent()
    {
        var prompt = Parameter("prompt");

        var values = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        values.Should().BeEquivalentTo(new[] { "select_account", "consent" });
    }

    /// <summary>
    /// OAuth 2.0 spells a list of prompts as one space-separated parameter, so the space has to
    /// survive as a space. It reaches Google percent-encoded; a raw space in a query string is not a
    /// URL, and a <c>+</c> would be read literally by a parser that is not form-decoding.
    /// </summary>
    [Fact]
    public void The_two_prompt_values_travel_as_one_space_separated_parameter()
    {
        Query(state: "csrf-state").Should().Contain("prompt=select_account%20consent");

        // One parameter, not two — Google reads the last of a repeated key and the first value would
        // vanish without anything failing.
        QueryHelpers.ParseQuery(Query(state: "csrf-state"))["prompt"].Should().HaveCount(1);
    }

    /// <summary>
    /// Reconnecting an account names it, so Google's chooser opens on the account the operator
    /// pressed rather than on whichever one the browser session happens to hold.
    /// </summary>
    [Fact]
    public void A_reconnection_carries_the_account_it_is_about_as_a_login_hint()
    {
        Parameter("login_hint", loginHint: "pool-a2@example.com").Should().Be("pool-a2@example.com");

        // And the chooser is still asked for. The hint is a preselection, not a lock: an operator
        // who meant A2 and is shown A1 has to be able to see that and choose again.
        Parameter("prompt", loginHint: "pool-a2@example.com").Should().Be(GoogleOAuthUrls.Prompt);
    }

    /// <summary>
    /// Adding an account names none. An empty <c>login_hint</c> is a hint that points at nothing,
    /// and what Google does with one is not worth discovering on the operator's screen.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Adding_an_account_sends_no_login_hint_at_all(string? loginHint)
    {
        QueryHelpers.ParseQuery(Query(state: "csrf-state", loginHint))
            .Should().NotContainKey("login_hint");
    }

    private static string Query(string state, string? loginHint = null) =>
        new Uri(GoogleOAuthUrls.BuildAuthorizationUrl(Configured, state, loginHint)).Query;

    private static string Parameter(string name, string? loginHint = null) =>
        QueryHelpers.ParseQuery(Query("csrf-state", loginHint))[name].ToString();

    [Fact]
    public void Configuration_that_is_missing_a_secret_is_not_configured()
    {
        new GoogleOAuthOptions().IsConfigured().Should().BeFalse();

        new GoogleOAuthOptions
        {
            ClientId = "a",
            RedirectUri = "b",
        }.IsConfigured().Should().BeFalse();

        new GoogleOAuthOptions
        {
            ClientId = "a",
            ClientSecret = "b",
            RedirectUri = "c",
        }.IsConfigured().Should().BeTrue();
    }
}
