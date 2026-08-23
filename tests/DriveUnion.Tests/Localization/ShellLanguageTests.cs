using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// The shell itself, rendered in both languages by the real pipeline.
///
/// Every assertion reads HTML-decoded text: Razor's default encoder writes everything outside Basic
/// Latin as a numeric character reference, so an assertion against «فایل‌ها» in the raw markup
/// passes on a page that says it and on a page that does not.
/// </summary>
public class ShellLanguageTests
{
    /// <summary>Anything in the Arabic script block, which is what both Persian and its digits sit in.</summary>
    private const string ArabicScriptRun = "[\u0600-\u06FF\u200C]+";

    [Fact]
    public async Task The_persian_shell_is_what_it_has_always_been()
    {
        await using var harness = new LocalizationHarness();

        var html = await harness.SignedInShellAsync("tenant");

        html.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");
        html.Should().Contain("درایو یونیون");
        html.Should().Contain("داشبورد");
        html.Should().Contain("فایل‌ها");
        html.Should().Contain("لینک‌های اشتراک");
        html.Should().Contain(">خروج<", "the sidebar's sign-out control is the shell's own");
        html.Should().Contain("آپلود فایل");
    }

    [Fact]
    public async Task The_english_shell_is_the_same_shell_in_english()
    {
        await using var harness = new LocalizationHarness();

        var html = await harness.SignedInShellAsync("tenant", acceptLanguage: "en");

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
        html.Should().Contain("Drive Union");
        html.Should().Contain("Dashboard");
        html.Should().Contain("</span>Files");
        html.Should().Contain("Share links");
        html.Should().Contain(">Sign out<");
        html.Should().Contain("Upload file");
    }

    /// <summary>
    /// The migration's real test: nothing Persian is left on an English page except the one word
    /// that is supposed to be — the switch back, named in its own script so a reader of neither can
    /// still find it.
    ///
    /// A literal left behind in a view is invisible to every other assertion here, because it
    /// renders perfectly; this is the one that sees it.
    /// </summary>
    [Fact]
    public async Task The_only_persian_left_on_an_english_page_is_the_way_back_to_persian()
    {
        await using var harness = new LocalizationHarness();

        var html = await harness.SignedInShellAsync("tenant", acceptLanguage: "en");

        var runs = Regex.Matches(html, ArabicScriptRun, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        runs.Should().ContainSingle().Which.Should().Be("فارسی");
    }

    [Fact]
    public async Task The_sign_in_page_is_in_english_too()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync(acceptLanguage: "en");

        html.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
        html.Should().Contain("Sign in to the panel");
        html.Should().Contain("Accounts are created by the operator");

        // Somebody who cannot read Persian meets this page first, so the switch has to be on it —
        // and the only Persian on it is the switch's own label.
        var runs = Regex.Matches(html, ArabicScriptRun, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        runs.Should().ContainSingle().Which.Should().Be("فارسی");
    }

    [Fact]
    public async Task The_first_run_screen_is_in_english_too()
    {
        await using var harness = new LocalizationHarness();

        // No operator seeded, so the sign-in address renders the first-run setup screen.
        var html = await harness.ShellAsync(acceptLanguage: "en");

        html.Should().Contain("This panel has no operator yet");
        html.Should().Contain("at least 10 characters", "the policy is read from IdentityOptions in either language");
        html.Should().Contain("Repeat the password");
    }

    /// <summary>
    /// What the operator's half of the sidebar says, in both languages. The pool's own words are
    /// behind the operator claim, so this is also the only place they can be read at all.
    /// </summary>
    [Fact]
    public async Task The_operators_sidebar_names_the_pool_in_both_languages()
    {
        await using var harness = new LocalizationHarness();

        var persian = await harness.SignedInShellAsync("operator");
        persian.Should().Contain("اکانت‌های گوگل");
        persian.Should().Contain("سهمیه آپلود امروز");
        persian.Should().Contain("ربات تلگرام");

        var english = await harness.SignedInShellAsync("operator", acceptLanguage: "en");
        english.Should().Contain("Google accounts");
        english.Should().Contain("Today's upload quota");
        english.Should().Contain("Telegram bot");
    }

    /// <summary>
    /// The Telegram screens had no way in but the address bar. The slot is the comp's «تنظیمات»,
    /// and which of the two addresses it points at is which half of the panel is looking — the same
    /// distinction the sidebar already draws the Google pool behind.
    /// </summary>
    [Fact]
    public async Task Both_halves_of_the_panel_can_reach_their_own_telegram_screen()
    {
        await using var harness = new LocalizationHarness();

        var forOperator = await harness.SignedInShellAsync("operator");
        forOperator.Should().Contain("href=\"/telegram\"");
        forOperator.Should().NotContain("href=\"/telegram/link\"", "the operator has no account to link");

        var forCustomer = await harness.SignedInShellAsync("tenant");
        forCustomer.Should().Contain("href=\"/telegram/link\"");
        forCustomer.Should().NotContain("ربات تلگرام", "the bot's configuration is the operator's");
    }

    [Fact]
    public async Task An_anonymous_shell_offers_neither()
    {
        await using var harness = new LocalizationHarness();
        harness.SeedOperator();

        var html = await harness.ShellAsync();

        // The sign-in page wears this shell, and a nav item that can only challenge is a control
        // that does nothing.
        html.Should().NotContain("href=\"/telegram");
        html.Should().Contain("تنظیمات");
    }
}
