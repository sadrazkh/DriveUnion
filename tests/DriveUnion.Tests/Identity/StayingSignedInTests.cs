using System.Net;
using DriveUnion.Tests.Localization;
using DriveUnion.Web.Localization;
using FluentAssertions;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// Whether the panel is still signed in the next time it is opened.
///
/// <para>The bug these exist for is only visible on a phone. Installed on an iPhone home screen the
/// panel gets its own cookie jar, and iOS ends that jar's browser session whenever it evicts the app
/// from memory — which is most times the phone is put down. Every ordinary sign-in used to produce a
/// cookie with no expiry of its own, so the password screen came back on nearly every cold open and
/// the installed app was unusable.</para>
///
/// <para>The fix is two settings that have to be read together, so they are asserted together: the
/// cookie has an explicit thirty-day sliding life, and the sign-in form asks for a persistent one by
/// default. Either on its own changes nothing a phone can see — <c>ExpireTimeSpan</c> has always
/// bounded the ticket, and a ticket is only written to disk when the sign-in was persistent.</para>
///
/// <para>What a longer credential costs, and the reason it is affordable at all, is
/// <c>DisabledMemberTests.A_long_lived_cookie_does_not_outlive_being_disabled</c>.</para>
/// </summary>
public class StayingSignedInTests
{
    [Fact]
    public void The_panel_cookie_lasts_thirty_days_and_its_clock_restarts_on_use()
    {
        using var harness = new IdentityPagesHarness();

        var cookie = harness.PanelCookie();

        // Pinned rather than inherited. Both of these are also framework defaults in spirit —
        // fourteen days, sliding — and the whole point of Program.cs setting them is that the number
        // is a decision somebody made and argued, so a change to it should have to come through here.
        cookie.ExpireTimeSpan.Should().Be(TimeSpan.FromDays(30));
        cookie.SlidingExpiration.Should().BeTrue();

        // Left at Identity's defaults on purpose, and asserted so that "left alone" stays a decision.
        // They are two thirds of what makes thirty days payable: a credential that cannot be read
        // from script and does not ride along on somebody else's cross-site request.
        cookie.Cookie.HttpOnly.Should().BeTrue();
        cookie.Cookie.SameSite.Should().Be(Microsoft.AspNetCore.Http.SameSiteMode.Lax);
    }

    [Fact]
    public async Task A_sign_in_writes_a_cookie_that_outlives_the_app_being_evicted()
    {
        using var harness = new IdentityPagesHarness();
        var user = await harness.CreateOperatorWithPasswordAsync();

        using var client = harness.NewClient(keepCookies: true);

        using var signedIn = await IdentityPagesHarness.SignInAsync(
            client, user.Email!, IdentityPagesHarness.Password, rememberMe: true);

        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var cookie = harness.PanelCookieOn(signedIn);

        cookie.Should().NotBeNull("signing in has to write the panel's cookie");

        // The whole of the bug, in one assertion. Without an Expires the browser holds this in
        // memory and throws it away with the session — which on an installed iOS web app is every
        // time the system reclaims the RAM, not every time somebody quits the browser.
        cookie!.Expires.Should().NotBeNull(
            "a cookie with no expiry dies with the browser session, and an installed iOS web app "
            + "loses its session whenever iOS evicts it from memory");

        cookie.Expires!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(harness.PanelCookie().ExpireTimeSpan),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// The expiry moves forward as the panel is used, which is the whole of "sliding" and the reason
    /// thirty days is enough: a phone opened every week or two never reaches the end of the window.
    ///
    /// <para>It renews on <i>every</i> authenticated request here, not only past the halfway point
    /// the cookie handler's own sliding rule waits for. That is
    /// <c>SecurityStampValidatorOptions.ValidationInterval = TimeSpan.Zero</c> doing it: the stamp is
    /// re-read on every request, and a validator that has re-read the row asks for the cookie to be
    /// reissued. So the window is really thirty days from the last request rather than thirty days
    /// from signing in — which is better for the phone, and is also why the number must be one that
    /// is safe to keep restarting.</para>
    /// </summary>
    [Fact]
    public async Task Using_the_panel_pushes_the_expiry_back_out()
    {
        using var harness = new IdentityPagesHarness();
        var user = await harness.CreateOperatorWithPasswordAsync();

        using var client = harness.NewClient(keepCookies: true);

        using var signedIn = await IdentityPagesHarness.SignInAsync(
            client, user.Email!, IdentityPagesHarness.Password, rememberMe: true);

        var atSignIn = harness.PanelCookieOn(signedIn)!.Expires!.Value;

        // An ordinary panel page, guarded by the operator policy — so reaching it is the cookie
        // being read and the principal rebuilt, which is the request the renewal rides on.
        using var page = await client.GetAsync(new Uri("/accounts", UriKind.Relative));

        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var reissued = harness.PanelCookieOn(page);

        reissued.Should().NotBeNull("using the panel has to push the expiry back out, or the window "
            + "counts from the sign-in and a phone reaches the end of it while still in daily use");

        reissued!.Expires.Should().NotBeNull();
        reissued.Expires!.Value.Should().BeOnOrAfter(atSignIn);
    }

    /// <summary>
    /// The other half of defaulting the box to on. If unticking it did nothing, the control would be
    /// a lie on the one screen where somebody is deciding what to leave behind on a borrowed machine
    /// — and it would fail silently, because the cookie looks the same to everybody but the browser.
    /// </summary>
    [Fact]
    public async Task Unticking_the_box_leaves_a_cookie_that_dies_with_the_browser()
    {
        using var harness = new IdentityPagesHarness();
        var user = await harness.CreateOperatorWithPasswordAsync();

        using var client = harness.NewClient(keepCookies: true);

        using var signedIn = await IdentityPagesHarness.SignInAsync(
            client, user.Email!, IdentityPagesHarness.Password, rememberMe: false);

        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var cookie = harness.PanelCookieOn(signedIn);

        cookie.Should().NotBeNull();

        // No expiry at all — a session cookie, which is exactly the behaviour this change replaced,
        // kept for the case it was always right for. Reaching this assertion also proves the hidden
        // companion field in Login.cshtml survives: an unticked checkbox posts nothing, so without
        // it the model binder would find no value and leave RememberMe at its initialiser, which is
        // now true.
        cookie!.Expires.Should().BeNull();
        cookie.MaxAge.Should().BeNull();
    }

    [Fact]
    public async Task The_form_ticks_the_box_and_says_what_ticking_it_means()
    {
        using var harness = new IdentityPagesHarness();
        harness.SeedOperator();

        using var client = harness.NewClient();
        using var response = await client.GetAsync(new Uri(IdentityPagesHarness.LoginPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain(
            "name=\"RememberMe\" value=\"true\" checked",
            "the box is ticked when the form is drawn — that is the half of this a phone can see");

        html.Should().Contain(
            "type=\"hidden\" name=\"RememberMe\" value=\"false\"",
            "an unticked checkbox posts nothing, so without this the box could not be turned off");
    }

    /// <summary>
    /// The sentence under the box, in both languages, with the number taken from the cookie rather
    /// than from the sentence. A form that promised thirty days while the deployment granted seven
    /// would be worse than a form that said nothing at all.
    /// </summary>
    [Fact]
    public async Task The_form_says_how_long_in_whichever_language_it_is_read_in()
    {
        using var harness = new IdentityPagesHarness();
        harness.SeedOperator();

        var days = (int)Math.Round(harness.PanelCookie().ExpireTimeSpan.TotalDays);

        using var english = harness.NewClient();
        english.DefaultRequestHeaders.Add("Accept-Language", "en");

        using var inEnglish = await english.GetAsync(
            new Uri(IdentityPagesHarness.LoginPath, UriKind.Relative));

        var englishText = await LocalizationHarness.TextAsync(inEnglish);

        englishText.Should().Contain(Said(CultureScope.English, days));

        // Persian is the panel's default, so a bare request gets it. Decoded first: Razor writes
        // everything outside Basic Latin as a numeric character reference, so asserting on the raw
        // markup would pass against a page that never said this.
        using var bare = harness.NewClient();
        using var inPersian = await bare.GetAsync(
            new Uri(IdentityPagesHarness.LoginPath, UriKind.Relative));

        var persianText = await LocalizationHarness.TextAsync(inPersian);

        persianText.Should().Contain(Said(CultureScope.Persian, days));
    }

    private static string Said(Func<CultureScope> culture, int days)
    {
        using var scope = culture();

        return UiText.SignIn.StaySignedInHint(days);
    }
}
