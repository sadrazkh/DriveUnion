using DriveUnion.Core.Abstractions;
using DriveUnion.Web.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The CSRF state on the operator's consent flow. The popup work must not have cost any of this:
/// the nonce is still 32 random bytes in an HttpOnly cookie scoped to /accounts, and a callback
/// that cannot produce it is still refused before a code is exchanged for anything.
/// </summary>
public class GoogleConnectStateTests
{
    /// <summary>32 bytes, base64url, unpadded — plus the four-character mode prefix.</summary>
    private const int NonceLength = 43;

    [Fact]
    public void Connect_issues_a_state_cookie_the_browser_cannot_read_or_send_elsewhere()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Http.Request.IsHttps = true;

        var result = harness.Controller.Connect(popup: false);

        var cookie = harness.IssuedCookie();
        cookie.Should().NotBeNull();
        cookie!.HttpOnly.Should().BeTrue("script must not be able to read the state it is being asked to match");
        cookie.Secure.Should().BeTrue("the request arrived over HTTPS");
        cookie.Path.ToString().Should().Be("/accounts");

        // Lax and not Strict: Google sends the operator back with a top-level GET from another site,
        // and Strict would withhold the cookie on exactly that request.
        cookie.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        cookie.MaxAge.Should().Be(TimeSpan.FromMinutes(10));

        var state = cookie.Value.ToString();
        state.Should().HaveLength(4 + NonceLength);

        // The same value goes to Google, or there is nothing to compare on the way back.
        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        QueryHelpers.ParseQuery(new Uri(redirect.Url).Query)["state"].ToString().Should().Be(state);
    }

    [Fact]
    public void Two_consents_never_carry_the_same_nonce()
    {
        var first = ConnectFlowHarness.Create();
        var second = ConnectFlowHarness.Create();

        first.Controller.Connect(popup: false);
        second.Controller.Connect(popup: false);

        first.IssuedState().Should().NotBe(second.IssuedState());
    }

    [Fact]
    public async Task A_matching_state_is_what_lets_the_code_be_exchanged()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: false);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        harness.Directory.ExchangedCode.Should().Be("4/auth-code");

        // The redirect_uri sent to Google and the one sent with the exchange come from the same
        // option: Google compares the strings and says nothing useful when they differ.
        harness.Directory.ExchangedRedirectUri.Should().Be(ConnectFlowHarness.RedirectUri);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");

        // Named, so an operator who has just answered Google's account chooser can see which account
        // they actually approved rather than a sentence that fits either answer.
        harness.TempData["Notice"].Should().Be("اکانت A1 متصل شد — pool@gmail.com");
    }

    [Theory]
    // A callback nobody was sent — the classic forged consent.
    [InlineData(null, "top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // The cookie is there and the caller guessed.
    [InlineData("top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "top.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    // Right nonce, wrong mode: the prefix is part of the value, so this is a mismatch like any other.
    [InlineData("top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "pop.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // Returned with no state at all.
    [InlineData("top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    public async Task A_state_that_does_not_match_is_refused_before_anything_is_exchanged(
        string? cookie,
        string? returned)
    {
        var harness = ConnectFlowHarness.Create();
        if (cookie is not null) harness.SendStateCookie(cookie);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: returned,
            error: null,
            CancellationToken.None);

        harness.Directory.ConnectCalls.Should().Be(0);
        harness.TempData["Error"].Should().Be("بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.");
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task A_matching_state_with_no_code_is_still_refused()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: false);
        harness.SendStateCookie(harness.IssuedState()!);

        await harness.Controller.Callback(
            code: null,
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        harness.Directory.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_state_cookie_is_spent_whether_or_not_it_matched()
    {
        var harness = ConnectFlowHarness.Create();
        harness.SendStateCookie("top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        await harness.Controller.Callback("4/auth-code", "nonsense", null, CancellationToken.None);

        // Deleting means expiring: a replayed callback must not find the nonce still sitting there.
        var deletion = Microsoft.Net.Http.Headers.SetCookieHeaderValue
            .ParseList(harness.Http.Response.Headers.SetCookie)
            .Single(c => c.Name == ConnectFlowHarness.StateCookie);

        deletion.Value.ToString().Should().BeEmpty();
        deletion.Expires.Should().BeBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Google_saying_the_operator_declined_is_reported_and_exchanges_nothing()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: false);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: null,
            state: harness.IssuedState(),
            error: "access_denied",
            CancellationToken.None);

        harness.Directory.ConnectCalls.Should().Be(0);
        harness.TempData["Error"].Should().Be("اتصال اکانت لغو شد.");
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task A_failed_exchange_says_so_rather_than_throwing()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Directory.ConnectFailure = new DriveApiException("token endpoint unreachable");
        harness.Controller.Connect(popup: false);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        harness.TempData["Error"].Should().Be("تبادل کد با گوگل ناموفق بود.");
        result.Should().BeOfType<RedirectToActionResult>();
    }

    /// <summary>
    /// The state of this machine, and of any deployment before its first account: reading
    /// <c>.Value</c> throws, and the accounts screen is the screen an operator opens to find that out.
    /// </summary>
    [Fact]
    public async Task Unconfigured_Google_leaves_the_accounts_page_renderable()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var model = (await harness.Controller.Index(CancellationToken.None))
            .Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<AccountsPageViewModel>().Subject;

        model.ConsentConfigured.Should().BeFalse();
        model.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void Unconfigured_Google_sends_nobody_to_a_consent_screen_that_cannot_work()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var result = harness.Controller.Connect(popup: false);

        harness.IssuedCookie().Should().BeNull("there is no consent to protect");
        harness.TempData["Error"].Should().Be("پیکربندی OAuth گوگل کامل نیست. اطلاعات آن را در صفحه‌ی اکانت‌ها وارد کنید.");
        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task A_callback_that_arrives_after_the_credentials_were_removed_is_refused()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        harness.SendStateCookie("top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        await harness.Controller.Callback(
            code: "4/auth-code",
            state: "top.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            error: null,
            CancellationToken.None);

        harness.Directory.ConnectCalls.Should().Be(0);
        harness.TempData["Error"].Should().Be("پیکربندی OAuth گوگل کامل نیست. اطلاعات آن را در صفحه‌ی اکانت‌ها وارد کنید.");
    }
}
