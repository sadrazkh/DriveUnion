using System.Globalization;
using DriveUnion.Web.Localization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// Which language a request is answered in, decided before a byte of HTML exists.
///
/// The order is cookie, then <c>?lang=</c>, then <c>Accept-Language</c>, then Persian — an explicit
/// choice beating an ambient guess at every step. Each rung is asserted against the *other* rungs
/// disagreeing, because a precedence test where only one signal is present proves nothing about
/// precedence.
/// </summary>
public class CultureResolutionTests
{
    /// <summary>
    /// The one that regressed within an hour of the mechanism being written, and the one that costs
    /// the most when it does: a Persian-first product that renders English for everybody who never
    /// asked, on nothing more than the server's own locale.
    /// </summary>
    [Fact]
    public async Task A_request_that_says_nothing_is_answered_in_persian()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync();

        html.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");
    }

    [Fact]
    public async Task Accept_language_english_is_honoured()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        // A real browser's header, not a bare tag: en-GB is not a supported culture and has to fall
        // back to its parent, which is.
        var html = await harness.ShellAsync(acceptLanguage: "en-GB,en;q=0.9");

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
    }

    [Fact]
    public async Task A_regional_persian_is_still_persian()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync(acceptLanguage: "fa-IR,fa;q=0.9,en;q=0.8");

        html.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");
    }

    [Fact]
    public async Task A_language_the_panel_does_not_speak_falls_back_to_persian()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        // Not English by default, which is the trap: the framework's own fallback chain walks a
        // culture up to its parent, and «anything unrecognised» must land on the product's language
        // rather than on whichever one happens to be listed first.
        var html = await harness.ShellAsync(acceptLanguage: "de-DE,de;q=0.9");

        html.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");
    }

    [Fact]
    public async Task The_query_string_outranks_accept_language()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync(acceptLanguage: "fa-IR,fa;q=0.9", query: "?lang=en");

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
    }

    [Fact]
    public async Task The_cookie_outranks_accept_language()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync(acceptLanguage: "fa-IR,fa;q=0.9", cultureCookie: "en");

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
    }

    /// <summary>
    /// The rule the panel orders differently from the public download page, on purpose.
    ///
    /// Over here the cookie is only ever written by the switch in the shell, so it *is* the operator
    /// clicking; a <c>?lang=</c> on a panel URL is a link somebody was sent. Over there <c>?lang=</c>
    /// is the visitor clicking, because there is nothing else to click. Both say the same thing — the
    /// explicit act wins — and they disagree about which signal is the explicit act.
    /// </summary>
    [Fact]
    public async Task The_stored_choice_outranks_a_language_on_the_url()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync(cultureCookie: "fa", query: "?lang=en");

        html.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");
    }

    /// <summary>
    /// The formatting culture is pinned to invariant in both languages and no request can move it.
    ///
    /// Asserted on the options object the middleware is handed, because the failure it prevents is
    /// invisible in a page assertion: with fa-IR on the thread, every remaining ToString() in the
    /// product silently swaps its decimal point for «٫» — including the ones inside the mono,
    /// dir="ltr" readouts an operator copies into a Google support ticket.
    /// </summary>
    [Fact]
    public void No_request_can_change_how_a_number_is_punctuated()
    {
        var options = new RequestLocalizationOptions();
        DriveUnionLocalizationExtensions.Configure(options);

        options.SupportedCultures.Should().ContainSingle()
            .Which.Should().BeSameAs(CultureInfo.InvariantCulture);

        options.DefaultRequestCulture.Culture.Should().BeSameAs(CultureInfo.InvariantCulture);
        options.DefaultRequestCulture.UICulture.Should().Be(PanelCulture.Persian);
    }

    [Fact]
    public void Only_the_two_languages_the_panel_is_written_in_are_offered()
    {
        var options = new RequestLocalizationOptions();
        DriveUnionLocalizationExtensions.Configure(options);

        options.SupportedUICultures.Should().BeEquivalentTo(PanelCulture.Supported);
    }

    /// <summary>
    /// The order itself, not just its consequences. The framework's default is query, cookie,
    /// header; this reverses the first two, and a future edit that merely adds a provider would
    /// otherwise put it back without anything failing.
    /// </summary>
    [Fact]
    public void The_providers_are_asked_in_the_order_the_panel_decided()
    {
        var options = new RequestLocalizationOptions();
        DriveUnionLocalizationExtensions.Configure(options);

        options.RequestCultureProviders.Select(p => p.GetType()).Should().Equal(
            typeof(CookieRequestCultureProvider),
            typeof(QueryStringRequestCultureProvider),
            typeof(AcceptLanguageHeaderRequestCultureProvider));

        options.RequestCultureProviders
            .OfType<QueryStringRequestCultureProvider>()
            .Single()
            .UIQueryStringKey
            .Should().Be("lang", "the public download page has always answered ?lang= and there is one spelling");
    }

    /// <summary>
    /// The ambient culture is not a vote.
    ///
    /// Outside a request — a background thread, a unit test, a server whose operating system is set
    /// to English — <see cref="CultureInfo.CurrentUICulture"/> is whatever was inherited. Only the
    /// exact tag the middleware assigns counts as somebody asking.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void A_machines_own_locale_does_not_make_the_panel_english(string ambient)
    {
        using var scope = new CultureScope(CultureInfo.GetCultureInfo(ambient));

        PanelCulture.IsPersian.Should().BeTrue();
        PanelCulture.Code.Should().Be("fa");
        PanelCulture.Direction.Should().Be("rtl");
    }

    [Fact]
    public void The_tag_the_middleware_assigns_is_what_switches_the_panel()
    {
        using (CultureScope.English())
        {
            PanelCulture.IsPersian.Should().BeFalse();
            PanelCulture.Code.Should().Be("en");
            PanelCulture.Direction.Should().Be("ltr");
            PanelCulture.OtherCode.Should().Be("fa");
        }

        using (CultureScope.Persian())
        {
            PanelCulture.IsPersian.Should().BeTrue();
            PanelCulture.OtherCode.Should().Be("en");
        }
    }

    [Theory]
    [InlineData("fa")]
    [InlineData("FA")]
    [InlineData("en")]
    public void A_tag_the_panel_speaks_is_parsed(string tag) =>
        PanelCulture.Parse(tag).Should().NotBeNull();

    [Theory]
    [InlineData("de")]
    [InlineData("fa-IR")]
    [InlineData("")]
    [InlineData(null)]
    public void A_tag_the_panel_does_not_offer_is_refused(string? tag) =>
        PanelCulture.Parse(tag).Should().BeNull();
}
