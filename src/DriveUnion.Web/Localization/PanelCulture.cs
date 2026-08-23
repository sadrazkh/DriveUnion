using System.Globalization;

namespace DriveUnion.Web.Localization;

/// <summary>
/// The two languages the panel is written in, and the one question every localised string asks.
///
/// The answer lives on <see cref="CultureInfo.CurrentUICulture"/>, which the request localisation
/// middleware sets from the cookie, then <c>?lang=</c>, then <c>Accept-Language</c> — see
/// <see cref="DriveUnionLocalizationExtensions"/>. Reading it here rather than threading a language
/// down through every view model is deliberate: the shell, a partial three levels deep and a
/// validation attribute that never sees an <c>HttpContext</c> all need the same answer, and a
/// parameter that has to be passed is a parameter somebody forgets to pass.
///
/// <see cref="CurrentUICulture"/> and not <see cref="CultureInfo.CurrentCulture"/>: the panel's
/// numbers and dates are formatted with the invariant culture on purpose (see
/// <c>DisplayFormats</c> and <c>PersianDigits</c>), and letting a request change how a byte size is
/// punctuated is a silent bug in an operator's readout.
/// </summary>
public static class PanelCulture
{
    /// <summary>The product's default. Everything falls back to this, including a culture we do not know.</summary>
    public const string PersianCode = "fa";

    public const string EnglishCode = "en";

    public static readonly CultureInfo Persian = CultureInfo.GetCultureInfo(PersianCode);

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo(EnglishCode);

    /// <summary>What the panel is willing to render. Order is not significant.</summary>
    public static IReadOnlyList<CultureInfo> Supported { get; } = [Persian, English];

    /// <summary>
    /// True unless this request was resolved to English — and "resolved" is the whole of it.
    ///
    /// The test is an exact match on <see cref="EnglishCode"/> and deliberately not "does this
    /// culture look English". <see cref="CultureInfo.CurrentUICulture"/> is ambient: outside a
    /// request it is whatever the operating system, the test runner or a background thread happened
    /// to inherit, and on a Windows box that is <c>en-US</c>. None of those is a visitor asking for
    /// English, and treating them as one turns a Persian-first product English for everybody who
    /// never asked — silently, and only on somebody else's machine.
    ///
    /// The request localisation middleware only ever assigns one of <see cref="Supported"/>, so
    /// inside a request the value is exactly <c>fa</c> or exactly <c>en</c>. Anything else —
    /// <c>en-US</c>, <c>de-DE</c>, the invariant culture, a thread the middleware never touched —
    /// is not an answer, and the product's own language is what an unanswered question renders as.
    /// It is also why this change is safe to ship before Program.cs calls
    /// <c>UseRequestLocalization</c>: with no middleware at all, every page is Persian, exactly as
    /// it was.
    /// </summary>
    public static bool IsPersian => !IsEnglish(CultureInfo.CurrentUICulture);

    /// <summary>The BCP 47 tag for <c>&lt;html lang&gt;</c>.</summary>
    public static string Code => IsPersian ? PersianCode : EnglishCode;

    /// <summary>The value for <c>&lt;html dir&gt;</c> — the whole of what the stylesheet needs.</summary>
    public static string Direction => IsPersian ? "rtl" : "ltr";

    /// <summary>What the language switch offers, which is always the one you are not reading.</summary>
    public static string OtherCode => IsPersian ? EnglishCode : PersianCode;

    /// <summary>
    /// The culture a language tag names, or null when the panel does not speak it.
    ///
    /// Used by the switch endpoint to refuse a value it was not offered, so a hand-written POST
    /// cannot park an unsupported tag in a visitor's cookie for a year.
    /// </summary>
    public static CultureInfo? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        foreach (var culture in Supported)
        {
            if (string.Equals(tag, culture.Name, StringComparison.OrdinalIgnoreCase)) return culture;
        }

        return null;
    }

    /// <summary>
    /// The exact tag, not the language family. <c>en-US</c> is a machine's locale; <c>en</c> is
    /// what this panel resolves a request to. If a regional English is ever added to
    /// <see cref="Supported"/>, this is the line that has to learn about it.
    /// </summary>
    private static bool IsEnglish(CultureInfo culture) =>
        string.Equals(culture.Name, English.Name, StringComparison.OrdinalIgnoreCase);
}
