using DriveUnion.Core.Sharing;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Web.Models;

public enum PublicLanguage
{
    Fa = 0,
    En = 1,
}

/// <summary>
/// Which language the public page is rendered in, decided before any HTML leaves the server.
///
/// It is a server decision and not a toggle in script because the page has to be readable with
/// JavaScript off, and because a cached copy has to be cacheable per language rather than per
/// visitor's local storage.
/// </summary>
public static class PublicLanguageResolver
{
    public static PublicLanguage Resolve(string? requested, string? acceptLanguage)
    {
        // ?lang= wins: it is the visitor clicking FA/EN, which outranks whatever their browser was
        // configured with years ago.
        if (TryMatch(requested, out var chosen)) return chosen;

        if (string.IsNullOrWhiteSpace(acceptLanguage)) return PublicLanguage.Fa;
        if (!StringWithQualityHeaderValue.TryParseList([acceptLanguage], out var accepted) || accepted is null)
        {
            return PublicLanguage.Fa;
        }

        foreach (var entry in accepted.OrderByDescending(value => value.Quality ?? 1d))
        {
            if (entry.Quality is 0) continue;
            if (TryMatch(entry.Value.Value, out var match)) return match;
        }

        return PublicLanguage.Fa;
    }

    private static bool TryMatch(string? tag, out PublicLanguage language)
    {
        language = PublicLanguage.Fa;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        if (IsTag(tag, "fa")) return true;

        if (IsTag(tag, "en"))
        {
            language = PublicLanguage.En;
            return true;
        }

        return false;
    }

    // "fa" and "fa-IR" are Persian; "fao" is Faroese. A bare StartsWith would confuse them.
    private static bool IsTag(string tag, string language) =>
        tag.Equals(language, StringComparison.OrdinalIgnoreCase)
        || (tag.Length > language.Length
            && tag[language.Length] == '-'
            && tag.AsSpan(0, language.Length).Equals(language, StringComparison.OrdinalIgnoreCase));
}

public static class PublicText
{
    public static string Pick(PublicLanguage language, string fa, string en) =>
        language == PublicLanguage.Fa ? fa : en;

    public static string LangCode(PublicLanguage language) => language == PublicLanguage.Fa ? "fa" : "en";

    public static string Direction(PublicLanguage language) => language == PublicLanguage.Fa ? "rtl" : "ltr";
}

public sealed record PublicDownloadViewModel(
    PublicLanguage Language,
    string FileName,
    string SizeText,
    string KindText,
    string DateText,
    string ExpiryText,
    string DownloadCountText,
    string DisplayUrl,
    string DownloadUrl,
    /// <summary>
    /// The header, as JSON, for a file that was locked before it was uploaded — and null for one
    /// that was not.
    ///
    /// <para>JSON rather than the record, because the only thing on this page that can act on it is
    /// the script that does the decrypting, and a string the view writes into a data attribute is
    /// how every other island on this site is handed its input. The C# side reads none of it.</para>
    /// </summary>
    string? EncryptionJson = null,

    /// <summary>The workspace that shared it, or empty when there is nothing to say.</summary>
    string SharedBy = "",

    /// <summary>The sender's line for this link's recipients. Razor encodes it; see the view.</summary>
    string? Note = null,

    /// <summary>What may be drawn, decided in <c>Previews</c> and never in the view.</summary>
    PreviewKind Preview = PreviewKind.None,

    /// <summary>Where the inline bytes are, for the one of those that is not <c>None</c>.</summary>
    string PreviewUrl = "",

    /// <summary>
    /// The public address on its own, for the «report this link» link in the footer.
    ///
    /// <para>Carried rather than parsed back out of <c>DownloadUrl</c>: two spellings of one fact
    /// that a change to either would silently separate.</para>
    /// </summary>
    string Slug = "",

    /// <summary>
    /// What a locked file would be if the lock came off, and <c>None</c> for one that is not locked.
    ///
    /// <para>Separate from <see cref="Preview"/>, which is <c>None</c> for everything encrypted and
    /// is right to be: the server holds ciphertext. This one is what decides whether the unlock card
    /// offers to <i>play</i> the file after the passphrase rather than only to save it — a question
    /// only the browser can act on, because only the browser has the key.</para>
    ///
    /// <para>Always <c>None</c> when <see cref="EncryptionJson"/> is null. An unlocked file that can
    /// be drawn is drawn by <see cref="Preview"/> already, and offering a second player for it would
    /// be two of them on one card.</para>
    /// </summary>
    PreviewKind UnlockedMedia = PreviewKind.None,

    /// <summary>
    /// The recorded type, for the element the browser will build. Empty when there is nothing to play.
    ///
    /// <para>It is the type the uploader's browser claimed, which is an intent rather than a fact —
    /// but by the time it is used the bytes have been decrypted in this reader's own tab and are
    /// being handed to a media element, so a wrong type is a file that will not play rather than
    /// anything that can act.</para>
    /// </summary>
    string MimeType = "",

    /// <summary>
    /// «video», «audio», or empty — for an <b>unlocked</b> file too big to preview.
    ///
    /// <para><see cref="Preview"/> is <c>None</c> for anything past <c>MostBytesToShowWhole</c>, and
    /// that ceiling is right: a preview spends no download, so without it a link capped at five
    /// could be drained by loading the page. What it also did was make a film — the thing this
    /// product is for — the one kind of file you could not watch without downloading it first.</para>
    ///
    /// <para>So a big film gets a play button instead of an automatic preview, and the button plays
    /// from the ordinary download address rather than the preview one. That keeps the ceiling's
    /// argument intact by agreeing with it: watching the film <i>is</i> taking the file, so pressing
    /// play spends a download exactly as pressing Download does. Seeking afterwards does not —
    /// <c>DownloadCounting</c> already draws that line, and it draws it in the right place.</para>
    /// </summary>
    string BigMedia = "",

    /// <summary>
    /// The player's own address, for a file too big to preview.
    ///
    /// <para>A page and not a box on this card. The card is a narrow column with a title, four
    /// facts, a note and a download button in it, and a film wedged above them is what this was —
    /// on a phone the controls landed on the text.</para>
    /// </summary>
    string WatchUrl = "",

    /// <summary>
    /// The card a chat client draws when this link is pasted into it.
    ///
    /// <para>On this record and on no other, which is the whole of the design — see
    /// <see cref="PublicUnfurl"/>.</para>
    /// </summary>
    PublicUnfurl? Unfurl = null);

/// <summary>
/// What <c>/d/{slug}</c> unfurls into when somebody pastes it into Telegram, WhatsApp or Twitter:
/// the Open Graph and Twitter-card tags, as four facts rather than as markup.
///
/// <para><b>Only a live link has one.</b> This type exists on
/// <see cref="PublicDownloadViewModel"/> alone. <see cref="PublicUnavailableViewModel"/>,
/// <see cref="PublicOverTrafficViewModel"/> and <see cref="AbuseReportViewModel"/> have no field
/// that could carry it and no view that could render it, and that is deliberate: a revoked link
/// that still unfurls with the file's name in it leaks exactly what revoking was for. It leaks it
/// to everybody in the channel rather than to the one person who followed the link, and it leaks it
/// into a third party's cache, where it outlives the request that produced it.</para>
///
/// <para>The alternative — one block of tags in <c>_PublicLayout</c> behind a condition — is one
/// inverted <c>if</c> away from that leak, forever, and nothing about the mistake would be visible
/// on the page. So the tags are a Razor section that only <c>Views/Public/Download.cshtml</c>
/// defines, and this record is what feeds it.</para>
/// </summary>
/// <param name="Description">
/// Size · type · who shared it — and deliberately <b>not</b> the sender's note. See
/// <c>PublicDownloadController.UnfurlFor</c> for that argument.
/// </param>
/// <param name="Url">
/// The canonical address, absolute and built from the configured public base rather than from the
/// host this request arrived on. A crawler is told the address the customer sends people to.
/// </param>
/// <param name="ImageUrl">
/// The public preview route, absolute — and null for everything that is not an image the server has
/// already decided it may draw. Null means the tag is not written at all; a placeholder invented for
/// the occasion would be a picture of nothing dressed up as a thumbnail.
/// </param>
public sealed record PublicUnfurl(
    string Title,
    string Description,
    string Url,
    string? ImageUrl = null);

/// <summary>
/// The refusal card. It carries no slug, no reason and no file: revoked, expired, capped and
/// never-existed have to be one identical response, or the difference between them is an oracle for
/// walking the slug space.
/// </summary>
public sealed record PublicUnavailableViewModel(PublicLanguage Language);

/// <summary>
/// The card for a link whose owner has spent their month's traffic.
///
/// <para>It carries a language and nothing else — deliberately no figures, no workspace name and no
/// date. The visitor is a stranger who was handed a link; what the sender bought, how much of it is
/// gone and who they bought it from are none of their business, and a card that printed «۳۰۰ GB از
/// ۳۰۰ GB» would be putting a customer's commercial position on an anonymous page.</para>
///
/// <para>It is a second record rather than a flag on <see cref="PublicUnavailableViewModel"/>. That
/// one exists to make four refusals indistinguishable, and a variant of it is the beginning of the
/// fifth being told apart by accident — see <c>PublicDownloadController.OverAllowance</c> for why
/// this one is told apart on purpose.</para>
/// </summary>
public sealed record PublicOverTrafficViewModel(PublicLanguage Language);

/// <summary>
/// The abuse form, before and after.
/// </summary>
/// <param name="Sent">
/// True renders the acknowledgement instead of the form. One flag rather than two views, because
/// the two states share the heading, the layout and the language, and the only thing that differs
/// is whether there is anything left to fill in.
/// </param>
public sealed record AbuseReportViewModel(PublicLanguage Language, string Slug, bool Sent);
