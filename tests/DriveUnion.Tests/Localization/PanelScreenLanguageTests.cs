using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Accounts;
using DriveUnion.Tests.Http;
using DriveUnion.Tests.Links;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// The screens this slice migrated, rendered in both languages by the real pipeline.
///
/// <see cref="MigratedScreensTests"/> reads the source and refuses a Persian literal in it; that
/// catches the string that never reached the catalogue, and nothing else. It cannot see a page that
/// names the wrong entry, an entry whose two languages were swapped, or a figure that came out in
/// the numerals of the language it is not in. This asks the pages.
///
/// Every assertion here reads decoded text. Razor's encoder writes everything outside Basic Latin
/// as <c>&amp;#x641;…</c>, so <c>NotContain("فایل‌ها")</c> against raw markup passes on a page that
/// leaked the word — which is the exact failure this file exists to catch.
///
/// The harnesses are the ones the panel already had. A fourth would be a fourth thing to keep in
/// step with Program.cs.
/// </summary>
public class PanelScreenLanguageTests
{
    /// <summary>
    /// Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.
    /// Escaped rather than written out, like <see cref="MigratedScreensTests"/>: U+200C is invisible
    /// in an editor, and a class that had lost it would still compile and still match almost
    /// everything.
    /// </summary>
    private const string PersianCharacter = "[\u0600-\u06FF\u200C]";

    [Fact]
    public async Task The_file_table_is_persian_when_nothing_asks_otherwise()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var main = MainContent(await client.GetStringAsync(new Uri("/Files", UriKind.Relative)));

        main.Should().Contain("فایل‌ها", "Persian is the product's own language and its default");
        main.Should().Contain("نام").And.Contain("حجم").And.Contain("تغییر");
        main.Should().Contain("۱ فایل", "a count in Persian prose is written in Persian digits");
        main.Should().Contain("۱ لینک");

        // The file's own name is the customer's and is never touched by either language.
        main.Should().Contain("Q3-Report-Final.pdf");
    }

    [Fact]
    public async Task The_file_table_is_english_when_english_is_asked_for()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var main = MainContent(await client.GetStringAsync(new Uri("/Files?lang=en", UriKind.Relative)));

        main.Should().Contain("Files").And.Contain("Name").And.Contain("Size").And.Contain("Modified");

        // Agreement, which Persian does not have and English cannot do without: the same screen
        // holding two files has to say «files» and «links».
        main.Should().Contain("1 file").And.NotContain("1 files");
        main.Should().Contain("1 link").And.NotContain("1 links");

        // And nothing of the Persian is left anywhere in the page's own region. The shell's language
        // switch says «فارسی» on purpose and lives outside <main>, which is why this is scoped.
        NoPersianIn(main, "/Files?lang=en");
    }

    /// <summary>
    /// The rule the whole numeral mechanism is about, asked of one row: the figure in prose follows
    /// the prose, and the byte size beside it does not follow anything.
    /// </summary>
    [Fact]
    public async Task A_byte_size_is_latin_in_both_languages_and_the_count_beside_it_is_not()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);

        var persian = MainContent(await client.GetStringAsync(new Uri("/Files", UriKind.Relative)));
        var english = MainContent(await client.GetStringAsync(new Uri("/Files?lang=en", UriKind.Relative)));

        // PanelPageHarness seeds a 4096-byte file, which DisplayFormats writes as «4 KB».
        persian.Should().Contain("4 KB", "an operator copies this figure, so it is Latin in Persian too");
        english.Should().Contain("4 KB");

        persian.Should().Contain("۱ فایل");
        english.Should().Contain("1 file");
    }

    [Fact]
    public async Task The_links_table_is_persian_when_nothing_asks_otherwise()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant(
            "Acme",
            "Q3-Report-Final.pdf",
            "kx91mzq4",
            maxDownloads: 500,
            downloadCount: 241);

        using var client = harness.NewClient(tenant.Id);
        var main = MainContent(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));

        main.Should().Contain("لینک‌های اشتراک");
        main.Should().Contain("فایل").And.Contain("آدرس").And.Contain("وضعیت");
        main.Should().Contain("۲۴۱/۵۰۰", "the shipped Persian ratio, unchanged");
        main.Should().Contain("فعال");
        main.Should().Contain("/d/kx91mzq4", "a slug is a slug in every language");
    }

    [Fact]
    public async Task The_links_table_is_english_when_english_is_asked_for()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant(
            "Acme",
            "Q3-Report-Final.pdf",
            "kx91mzq4",
            maxDownloads: 500,
            downloadCount: 241);

        using var client = harness.NewClient(tenant.Id);
        var main = MainContent(await client.GetStringAsync(new Uri("/Links?lang=en", UriKind.Relative)));

        main.Should().Contain("Share links");
        main.Should().Contain("Address").And.Contain("Downloads").And.Contain("Expires").And.Contain("Status");
        main.Should().Contain("241/500", "the same ratio, in the numerals of the prose around it");
        main.Should().Contain("Active");
        main.Should().Contain("/d/kx91mzq4");

        NoPersianIn(main, "/Links?lang=en");
    }

    /// <summary>
    /// The status column's five words, which are the ones that had to be written rather than
    /// translated: «نزدیک سقف» and «سقف تکمیل» became <c>Near cap</c> and <c>Capped</c> because the
    /// column is 90px in the comp and the literal translations wrapped in it.
    /// </summary>
    [Fact]
    public async Task A_link_at_its_cap_says_so_in_words_that_fit_the_column()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant(
            "Acme",
            "Q3-Report-Final.pdf",
            "kx91mzq4",
            maxDownloads: 100,
            downloadCount: 76);

        using var client = harness.NewClient(tenant.Id);

        var persian = MainContent(await client.GetStringAsync(new Uri("/Links", UriKind.Relative)));
        var english = MainContent(await client.GetStringAsync(new Uri("/Links?lang=en", UriKind.Relative)));

        // ۷۶/۱۰۰ is the handoff's own amber row.
        persian.Should().Contain("نزدیک سقف");
        english.Should().Contain("Near cap");
    }

    /// <summary>
    /// The accounts screen with nothing configured — the first thing a new operator ever sees, and
    /// the screen with the most prose on it.
    /// </summary>
    [Fact]
    public async Task The_unconfigured_accounts_screen_is_persian_when_nothing_asks_otherwise()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var main = MainContent(await client.GetStringAsync("/accounts"));

        main.Should().Contain("اکانت‌های گوگل");
        main.Should().Contain("راه‌اندازی اتصال به گوگل");
        main.Should().Contain("هنوز اکانتی متصل نیست");
        main.Should().Contain("این پنل نمی‌تواند آن را بسازد");
        main.Should().Contain("هفت روز", "the seven-day trap is the reason this panel is written out at all");
    }

    [Fact]
    public async Task The_unconfigured_accounts_screen_is_english_when_english_is_asked_for()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        var main = MainContent(await client.GetStringAsync("/accounts?lang=en"));

        main.Should().Contain("Google accounts");
        main.Should().Contain("Set up the Google connection");
        main.Should().Contain("No account is connected yet");
        main.Should().Contain("this panel cannot make it for you");
        main.Should().Contain("seven days");

        // The sentence the mono «Client ID» box sits inside is assembled from two catalogue entries
        // because the term lands in a different place in each language. A swapped pair would render
        // as two half-sentences and nothing else would notice.
        main.Should().Contain("Google turns down every request that arrives without a");

        NoPersianIn(main, "/accounts?lang=en");
    }

    /// <summary>
    /// What is not translated, and is not supposed to be. Google compares these strings byte for
    /// byte, or a deployment types them into an environment; a localised one configures nothing.
    /// </summary>
    [Fact]
    public async Task Googles_own_spellings_are_the_same_in_both_languages()
    {
        using var harness = new OperatorPanelHarness(googleConfigured: false);
        using var client = harness.NewClient();

        foreach (var query in new[] { string.Empty, "?lang=en" })
        {
            var main = MainContent(await client.GetStringAsync("/accounts" + query));

            main.Should().Contain("Google:ClientId")
                .And.Contain("Google:ClientSecret")
                .And.Contain("Google:RedirectUri");
            main.Should().Contain("https://www.googleapis.com/auth/drive");
            main.Should().Contain(OperatorPanelHarness.OriginRedirectUri);
            main.Should().Contain("Testing").And.Contain("In production");
            main.Should().Contain("Web application").And.Contain("Google Drive API");
        }
    }

    /// <summary>
    /// The boundary this slice did not cross, pinned so crossing it is a decision rather than an
    /// accident.
    ///
    /// <c>/d/{slug}</c> resolves its own language in <c>PublicDownloadController</c> and its layout,
    /// <c>Views/Shared/_PublicLayout.cshtml</c>, builds the document's language, its FA/EN control
    /// and its <c>hreflang</c> alternates from <c>ViewData["Lang"]</c> rather than from
    /// <c>PanelCulture</c>. Localization/README.md says what folding the two together costs. Until
    /// it is done, the panel's culture cookie — which is written at <c>Path=/</c> and therefore does
    /// reach this route — must not quietly change what a visitor is handed, and <c>?lang=</c> stays
    /// the thing that decides.
    /// </summary>
    [Fact]
    public async Task The_public_download_page_still_answers_to_its_own_lang_and_not_to_the_panels_cookie()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", fileName: "quarterly-report.mp4");

        using var client = harness.NewClient();

        using var englishCookieOnPersianPage = new HttpRequestMessage(
            HttpMethod.Get,
            $"/d/{seeded.Slug}?lang=fa");
        englishCookieOnPersianPage.Headers.Add("Cookie", LocalizationHarness.CultureCookie("en"));

        using var response = await client.SendAsync(englishCookieOnPersianPage);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await LocalizationHarness.TextAsync(response);

        body.Should().Contain("به اشتراک گذاشته‌شده توسط", "?lang= is the visitor's own click and it wins here");
        body.Should().Contain("<html dir=\"rtl\" lang=\"fa\"");

        // And the other way: no cookie at all, ?lang=en, still the English card.
        var english = await LocalizationHarness.TextAsync(
            await client.GetAsync(new Uri($"/d/{seeded.Slug}?lang=en", UriKind.Relative)));

        english.Should().Contain("Shared by");
        english.Should().Contain("<html dir=\"ltr\" lang=\"en\"");
    }

    /// <summary>
    /// The guard can fail.
    ///
    /// Every <c>NoPersianIn</c> above would pass on a regex that matched nothing, on markup that was
    /// never decoded, and on a <c>&lt;main&gt;</c> the extractor returned empty. This runs the same
    /// three steps over the same page in the language that is full of Persian, and insists it finds
    /// some.
    /// </summary>
    [Fact]
    public async Task The_guard_that_finds_stray_persian_finds_persian_when_there_is_some()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var main = MainContent(await client.GetStringAsync(new Uri("/Files", UriKind.Relative)));

        main.Should().NotBeNullOrWhiteSpace("the extractor has to return the page's content region");

        Regex.Match(main, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Success.Should().BeTrue("the Persian file table is written in Persian");

        // And it is really the decoded text being searched, not the numeric character references
        // Razor writes — those are Basic Latin and would match nothing above while looking fine.
        main.Should().NotContain("&#x", "an assertion against encoded markup is an assertion against nothing");
    }

    private static void NoPersianIn(string main, string what)
    {
        var stray = Regex.Match(main, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5));

        stray.Success.Should().BeFalse(
            "{0} rendered a Persian character at index {1}, near «{2}» — an English page says nothing "
            + "in Persian inside its own content region",
            what,
            stray.Index,
            main.Substring(Math.Max(0, stray.Index - 30), Math.Min(70, main.Length - Math.Max(0, stray.Index - 30)))
                .ReplaceLineEndings(" "));
    }

    /// <summary>
    /// The page's own region, decoded.
    ///
    /// Scoped to <c>&lt;main&gt;</c> because the shell around it is deliberately bilingual on every
    /// page: the language switch is labelled in the language you are <em>not</em> reading, so an
    /// English panel carries the word «فارسی» in its header and always will.
    /// </summary>
    private static string MainContent(string html)
    {
        var match = Regex.Match(
            html,
            "<main class=\"app-content\">(.*)</main>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The page rendered no <main class=\"app-content\"> region.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
