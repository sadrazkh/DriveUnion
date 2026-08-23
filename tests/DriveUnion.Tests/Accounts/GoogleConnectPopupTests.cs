using DriveUnion.Core.Abstractions;
using DriveUnion.Web.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The popup flag's round trip through Google, and the two shapes the callback can answer with.
///
/// The flag rides the state cookie because that cookie is HttpOnly, scoped to /accounts and written
/// only by the antiforgery-protected POST — so these tests are as much about what cannot set it as
/// about what does.
/// </summary>
public class GoogleConnectPopupTests
{
    private const string PopupPrefix = "pop.";
    private const string TopLevelPrefix = "top.";

    [Theory]
    [InlineData(true, PopupPrefix)]
    [InlineData(false, TopLevelPrefix)]
    public void The_window_the_consent_started_in_is_recorded_in_the_state_cookie(bool popup, string prefix)
    {
        var harness = ConnectFlowHarness.Create();

        harness.Controller.Connect(popup);

        harness.IssuedState().Should().StartWith(prefix);
    }

    [Fact]
    public async Task A_consent_that_started_in_a_popup_comes_back_to_a_page_that_closes_itself()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: true);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("ConnectPopup");

        var model = view.Model.Should().BeOfType<ConnectPopupViewModel>().Subject;
        model.Succeeded.Should().BeTrue();
        model.Message.Should().Be("اکانت گوگل متصل شد.");

        // The opener reloads /accounts as the flow ends, so the same sentence has to be waiting for
        // it — the page must not end up silent about something the popup announced.
        harness.TempData["Notice"].Should().Be("اکانت گوگل متصل شد.");
    }

    [Fact]
    public async Task A_consent_that_started_in_the_panel_still_ends_as_a_redirect()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: false);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");
    }

    /// <summary>
    /// The point of keeping the flag out of the query string: a link a third party sends the operator
    /// cannot make the callback render a page that talks to <c>window.opener</c>.
    /// </summary>
    [Fact]
    public async Task A_state_invented_by_the_caller_cannot_choose_the_closing_page()
    {
        var harness = ConnectFlowHarness.Create();

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: PopupPrefix + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            error: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        harness.Directory.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_returned_state_that_claims_a_popup_over_a_top_level_cookie_is_a_mismatch()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: false);
        var issued = harness.IssuedState()!;
        harness.SendStateCookie(issued);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: PopupPrefix + issued[TopLevelPrefix.Length..],
            error: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>();
        harness.Directory.ConnectCalls.Should().Be(0);
        harness.TempData["Error"].Should().Be("بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.");
    }

    [Fact]
    public async Task Declining_at_Google_is_a_designed_card_rather_than_a_window_that_just_shuts()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Controller.Connect(popup: true);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: null,
            state: harness.IssuedState(),
            error: "access_denied",
            CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ConnectPopupViewModel>().Subject;

        model.Succeeded.Should().BeFalse();
        model.Title.Should().Be("اتصال لغو شد");
        model.Message.Should().Be("اتصال اکانت لغو شد.");
    }

    [Fact]
    public async Task A_mismatched_state_is_explained_inside_the_popup_it_happened_in()
    {
        var harness = ConnectFlowHarness.Create();
        harness.SendStateCookie(PopupPrefix + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: "something-else",
            error: null,
            CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ConnectPopupViewModel>().Subject;

        model.Succeeded.Should().BeFalse();
        model.Message.Should().Be("بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.");
        harness.Directory.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_exchange_is_explained_inside_the_popup()
    {
        var harness = ConnectFlowHarness.Create();
        harness.Directory.ConnectFailure = new DriveApiException("token endpoint unreachable");
        harness.Controller.Connect(popup: true);
        harness.SendStateCookie(harness.IssuedState()!);

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: harness.IssuedState(),
            error: null,
            CancellationToken.None);

        result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ConnectPopupViewModel>().Subject
            .Message.Should().Be("تبادل کد با گوگل ناموفق بود.");
    }

    /// <summary>
    /// The failure this machine meets first: no Google credentials at all. The popup opens, and it
    /// has to say why rather than flashing a blank window and vanishing.
    /// </summary>
    [Fact]
    public void Unconfigured_Google_fills_the_popup_it_was_opened_into()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var result = harness.Controller.Connect(popup: true);

        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ConnectPopupViewModel>().Subject;

        model.Succeeded.Should().BeFalse();
        model.Message.Should().Be("پیکربندی OAuth گوگل کامل نیست.");
        model.Hint.Should().Contain("Google:ClientId").And.Contain("Google:ClientSecret")
            .And.Contain("Google:RedirectUri");

        harness.IssuedCookie().Should().BeNull();
    }

    [Fact]
    public async Task A_callback_with_no_credentials_left_still_answers_the_popup_it_is_in()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        harness.SendStateCookie(PopupPrefix + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var result = await harness.Controller.Callback(
            code: "4/auth-code",
            state: PopupPrefix + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            error: null,
            CancellationToken.None);

        result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<ConnectPopupViewModel>().Subject
            .Hint.Should().NotBeNullOrEmpty();
    }
}
