using System.Globalization;
using DriveUnion.Web.Infrastructure;

namespace DriveUnion.Web.Localization;

/// <summary>
/// Numbers that sit inside prose, in whichever language the prose is.
///
/// The rule is <see cref="PersianDigits"/>'s and it has not changed: digits set in prose take that
/// prose's numerals, and digits in an LTR technical readout — byte sizes, transfer speeds, slugs,
/// Drive ids, addresses — stay Latin in every language, because those are values an operator
/// copies, greps or reads out to Google support.
///
/// What English changes is which side of that line most of the panel's numbers fall on. English
/// prose is LTR, so in English the two sides agree and every one of these methods is a plain Latin
/// formatting; the Persian path is untouched and still goes through <see cref="PersianDigits"/>.
/// This type exists so that a view never has to ask which language it is in to print a count.
///
/// A readout that is Latin in both languages does not belong here — call
/// <c>DisplayFormats</c> or format it invariantly, exactly as before.
/// </summary>
public static class Numerals
{
    /// <summary>Grouped count: «۱۴٬۲۸۶» / <c>14,286</c>. For anything a person counts.</summary>
    public static string Count(long value) => PanelCulture.IsPersian
        ? PersianDigits.Count(value)
        : value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Ungrouped integer: «۱۴۰۵» / <c>1405</c>. For years, day counts and other bare figures.</summary>
    public static string Plain(long value) => PanelCulture.IsPersian
        ? PersianDigits.Plain(value)
        : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Rounded percentage: «۶۸٪» / <c>68%</c>.</summary>
    public static string Percent(double value) => PanelCulture.IsPersian
        ? PersianDigits.Percent(value)
        : Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + '%';

    /// <summary>
    /// An already-assembled string — a formatted date, a duration, a sentence with a figure in it.
    /// Persian translates every digit and separator in it; English leaves it exactly as it is.
    /// </summary>
    public static string InProse(string? text) => PanelCulture.IsPersian
        ? PersianDigits.Translate(text)
        : text ?? string.Empty;
}
