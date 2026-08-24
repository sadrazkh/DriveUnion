using System.Globalization;

namespace DriveUnion.Core.Telegram;

/// <summary>
/// How the bot writes numbers, which follows the panel's rule rather than inventing a second one.
///
/// <para>The rule is direction, not language: <b>digits set in Persian prose are Persian, and digits
/// in an LTR technical readout stay Latin</b>. So a card's second line reads «۳ روز پیش» beside
/// <c>18.4 MB</c>, and «لینک‌ها (۲)» beside a link a customer will copy. It is the same call the panel
/// already makes on the same values, and a bot that made the opposite one would be the only surface in
/// the product that did.</para>
///
/// <para>The panel's own helper lives in the web project, which Core cannot see and should not: the
/// bot has no <c>HttpContext</c>, no view and no request culture. This is the same rule, spelled once
/// more, for the one other place the product speaks to somebody who is not looking at a screen.</para>
/// </summary>
public static class TelegramFormats
{
    private const char PersianZero = '۰';
    private const char ArabicDecimalSeparator = '٫';
    private const char ArabicThousandsSeparator = '٬';

    /// <summary>
    /// Decimal units, because the ceiling they are compared against is decimal and a card that said
    /// <c>1.9 GB</c> for a file the bot then refused would be telling the truth in the wrong base.
    /// </summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;

        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // Whole bytes have no decimal; everything above shows one, which is what «18.4 MB» and
        // «2.0 GB» both need and what keeps the column the same width on a phone.
        var text = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);

        return $"{text} {units[unit]}";
    }

    /// <summary>«۳ روز پیش» — Persian prose, and therefore Persian digits.</summary>
    public static string Ago(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment;

        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed < TimeSpan.FromMinutes(1)) return "همین حالا";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Digits((long)elapsed.TotalMinutes)} دقیقه پیش";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Digits((long)elapsed.TotalHours)} ساعت پیش";
        if (elapsed < TimeSpan.FromDays(30)) return $"{Digits((long)elapsed.TotalDays)} روز پیش";

        return $"{Digits((long)(elapsed.TotalDays / 30))} ماه پیش";
    }

    /// <summary>A bare count in Persian digits, for prose: «لینک‌ها (۲)».</summary>
    public static string Digits(long value) => Translate(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A grouped count in Persian digits: «۱۴٬۲۸۶».</summary>
    public static string Count(long value) => Translate(value.ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>Translates ASCII digits and separators in an already-assembled Persian string.</summary>
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
                    _ => c,
                };
            }
        });
    }
}
