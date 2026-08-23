using System.Globalization;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// Renders numbers in Persian-Indic digits, the way the handoff draws them.
///
/// The design mixes two digit systems on purpose and on the same screen: «۲۴۱ دانلود» and
/// «۱۴٬۲۸۶ آیتم» beside <c>18.4 MB</c>, <c>341 MB/s</c> and <c>/d/kx91mz</c>. The rule behind it is
/// direction, not language — digits set in Persian prose are Persian; digits in an LTR technical
/// readout (byte sizes, transfer speeds, latencies, slugs, Drive ids, addresses) stay Latin,
/// because those are values an operator copies, greps or reads out to Google support.
///
/// So this is a formatting decision per value, never a global one. Vazirmatn ships an "FD" variant
/// whose font substitutes every ASCII digit with a Persian glyph; it is deliberately not used —
/// it would turn the file sizes and the slugs Persian too, and there would be no way back.
///
/// Culture-independent by construction: grouped with the invariant culture and then translated, so
/// the output does not change when the thread's culture does.
/// </summary>
public static class PersianDigits
{
    private const char ArabicThousandsSeparator = '٬'; // ٬ — the handoff's «۱۴٬۲۸۶»
    private const char ArabicDecimalSeparator = '٫';   // ٫
    private const char ArabicPercentSign = '٪';        // ٪ — the handoff's «۶۸٪»
    private const char PersianZero = '۰';              // ۰ … ۹ are contiguous from here

    /// <summary>Grouped count, e.g. 14286 → «۱۴٬۲۸۶». Use for anything a person counts.</summary>
    public static string Count(long value) =>
        Translate(value.ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>Ungrouped integer, e.g. 1405 → «۱۴۰۵». Use for years and other bare figures.</summary>
    public static string Plain(long value) =>
        Translate(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Percentage with the Arabic percent sign, e.g. 68 → «۶۸٪». Rounded, because the comp shows
    /// whole percentages in prose and reserves the decimal ones for the progress bar's width.
    /// </summary>
    public static string Percent(double value) =>
        Translate(Math.Round(value).ToString("0", CultureInfo.InvariantCulture)) + ArabicPercentSign;

    /// <summary>
    /// Translates every ASCII digit and separator in <paramref name="text"/>. Use for strings that
    /// are already assembled — a formatted date, a duration, a template with a number in it.
    /// </summary>
    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c switch
                {
                    >= '0' and <= '9' => (char)(PersianZero + (c - '0')),
                    ',' => ArabicThousandsSeparator,
                    '.' => ArabicDecimalSeparator,
                    '%' => ArabicPercentSign,
                    _ => c,
                };
            }
        });
    }
}
