using DriveUnion.Infrastructure.Google;
using FluentAssertions;

namespace DriveUnion.Tests.Google;

public class GoogleOAuthUrlsTests
{
    [Fact]
    public void The_consent_url_asks_for_the_refresh_token_the_pool_depends_on()
    {
        var url = GoogleOAuthUrls.BuildAuthorizationUrl(
            new GoogleOAuthOptions
            {
                ClientId = "client-id.apps.googleusercontent.com",
                ClientSecret = "secret",
                RedirectUri = "https://drive.example/oauth/google",
            },
            state: "csrf-state");

        var query = Uri.UnescapeDataString(new Uri(url).Query);

        // Without these two, Google returns an access token and no refresh token — and the account
        // stops working an hour later, for reasons nothing in the panel can explain.
        query.Should().Contain("access_type=offline");
        query.Should().Contain("prompt=consent");

        query.Should().Contain("scope=https://www.googleapis.com/auth/drive");
        query.Should().Contain("redirect_uri=https://drive.example/oauth/google");
        query.Should().Contain("state=csrf-state");
        query.Should().Contain("response_type=code");
    }

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
