using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Links;

/// <summary>
/// The control that hands a finished share link to the phone's own share sheet.
///
/// <para>Everything this can hold is in the markup, and that is not a shortcoming of the test: the
/// decision the feature is made of — whether this browser has a sheet at all — belongs to
/// <c>navigator.share</c> and to nothing on the server. So what is checked here is the half the
/// server owns: that the button is drawn beside the copy button rather than instead of it, that it
/// arrives hidden so a browser without a sheet never shows one, that it carries the sentence a
/// recipient reads in the language its owner is reading, and that it is a sibling of the copy button
/// — which is how <c>copyLink.ts</c> finds it, and is a contract no build checks.</para>
/// </summary>
public class ShareSheetScreenTests
{
    private const string FileName = "Q3-Report-Final.pdf";

    /// <summary>
    /// Arabic and Persian letters and digits, plus the zero-width non-joiner Persian is full of.
    /// Escaped rather than written out, like <c>MigratedScreensTests</c>: U+200C is invisible in an
    /// editor, and a class that had lost it would still compile and still match almost everything.
    /// </summary>
    private const string PersianCharacter = "[\u0600-\u06FF\u200C]";

    /// <summary>
    /// The file name wrapped in the bidi isolate — U+2066 LRI and U+2069 PDI. Escaped for the reason
    /// above and then some: both characters render as nothing at all, in the source and on a screen.
    /// </summary>
    private const string IsolatedName = $"\u2066{FileName}\u2069";

    [Fact]
    public async Task The_share_button_is_drawn_beside_the_copy_button_and_not_instead_of_it()
    {
        var field = await FieldAsync();

        // Both, in the one box the address sits in. A desktop browser has no share sheet, so
        // replacing copy with share would take the only control there is away from most readers.
        field.Should().Contain(@"data-island=""copy-link""");
        field.Should().Contain("data-share-title=");
    }

    [Fact]
    public async Task The_two_buttons_are_siblings_because_that_is_how_the_script_reaches_the_second()
    {
        var field = await FieldAsync();

        // copyLink.ts is mounted on the copy button and finds the share button through
        // `parentElement`. Wrapping either one in a box for layout would leave the share button
        // permanently hidden — no error, no warning, and nothing else in the suite would notice.
        field.Should().MatchRegex(@"</button>\s*<button[^>]*data-share");
    }

    [Fact]
    public async Task The_share_button_arrives_hidden_because_only_the_browser_knows_whether_it_works()
    {
        var button = Button(await FieldAsync());

        // `navigator.share` is absent on every browser with no sheet and outside a secure context.
        // A server cannot tell, so it draws nothing and the script reveals it — the other way round
        // is a button that does nothing when pressed, and a reader who believes they have sent the
        // link.
        button.Should().Contain("hidden");
    }

    [Fact]
    public async Task The_share_button_carries_the_file_name_and_the_sentence_and_no_address_of_its_own()
    {
        var button = Button(await FieldAsync());

        // The subject line a mail target uses.
        button.Should().Contain($@"data-share-title=""{FileName}""");

        // And the first thing the recipient reads, above the link.
        button.Should().Contain("data-share-text=");

        // The address is on the copy button and is not repeated here. Two copies of it in one box
        // are two to keep in step, and the two disagreeing sends somebody a link to another file.
        button.Should().NotContain("data-value");
    }

    [Fact]
    public async Task A_file_with_no_link_has_nothing_to_share()
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", FileName, "kx91mzq4");
        var unlinked = harness.SeedFile(tenant.Id, "no-link-yet.pdf");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync($"/files?selected={unlinked.Id}");

        // The panel offers to make a link on this screen. A share button over a link that does not
        // exist would send an address that answers 404 to whoever was picked out of the sheet.
        markup.Should().NotContain("data-share-title");
        markup.Should().NotContain(@"data-island=""copy-link""");
    }

    [Fact]
    public async Task The_message_the_recipient_reads_is_written_in_the_owners_language()
    {
        var persian = Button(await FieldAsync());
        var english = Button(await FieldAsync("&lang=en"));

        persian.Should().Contain("برای شما");
        persian.Should().Contain("رسانی");

        english.Should().Contain("a file for you");
        english.Should().Contain(">Share<");

        // The refusal the script puts on the button when the sheet will not open travels with it,
        // for the same reason the wording does: a bundle is compiled once and cannot ask which
        // language this request was in.
        persian.Should().Contain("نشانی را کپی کنید");
        english.Should().Contain("copy the address");
    }

    [Fact]
    public async Task The_english_message_says_nothing_in_persian()
    {
        var english = Button(await FieldAsync("&lang=en"));

        Regex.Match(english, PersianCharacter, RegexOptions.None, TimeSpan.FromSeconds(5))
            .Success.Should().BeFalse(
                "this text is not a label on a screen, it is a message somebody sends — so its "
                + "language is the sender's, and an English panel must not put Persian in it");
    }

    [Fact]
    public async Task The_file_name_inside_the_persian_sentence_carries_its_own_direction()
    {
        var button = Button(await FieldAsync());

        // A file name is a Latin run inside a Persian sentence, which the bidirectional algorithm
        // lays out among the Persian words rather than where it was written — the same defect as
        // «GB 5» in a quota readout. There is no element around this string in the recipient's
        // messaging app to carry a `dir`, so the isolate has to travel inside the text itself.
        button.Should().Contain(IsolatedName);
    }

    /// <summary>
    /// The <c>.field</c> box on the detail panel of a file that has a live link, decoded.
    ///
    /// <para>Decoded because Razor's encoder writes everything outside Basic Latin as
    /// <c>&amp;#x641;…</c>, so an assertion in Persian against raw markup passes on a page that says
    /// it and on a page that does not. The isolate characters are encoded the same way.</para>
    /// </summary>
    private static async Task<string> FieldAsync(string query = "")
    {
        await using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", FileName, "kx91mzq4");

        Guid selected;

        using (var db = harness.NewDbContext())
        {
            selected = db.StoredFiles.AsNoTracking().First(f => f.TenantId == tenant.Id).Id;
        }

        using var client = harness.NewClient(tenant.Id);
        using var response = await client.GetAsync(
            new Uri($"/files?selected={selected}{query}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var markup = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // `.field` holds no element that is not a leaf, which is what lets a non-greedy match stop
        // at the end of the box rather than walking into the row of controls under it.
        var field = Regex.Match(
            markup,
            @"<div class=""field"" dir=""ltr"">(?<inside>.*?)</div>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(
            field.Success,
            "The detail panel rendered no address box for a file that has a live link.");

        return field.Groups["inside"].Value;
    }

    /// <summary>The share button, from its opening tag to the end of its label.</summary>
    private static string Button(string field)
    {
        var button = Regex.Match(
            field,
            "<button[^>]*data-share[^>]*>.*?</button>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(button.Success, $"No share button in the address box: «{field.Trim()}»");

        return button.Value;
    }
}
