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
    string PreviewUrl = "");

/// <summary>
/// The refusal card. It carries no slug, no reason and no file: revoked, expired, capped and
/// never-existed have to be one identical response, or the difference between them is an oracle for
/// walking the slug space.
/// </summary>
public sealed record PublicUnavailableViewModel(PublicLanguage Language);
