using System.Globalization;
using DriveUnion.Web.Infrastructure;

namespace DriveUnion.Web.Models;

/// <summary>
/// The handoff's number and date shapes: <c>18.4 MB</c>, «۱۴۰۵/۰۵/۳۱», «۳ روز پیش».
///
/// Sizes stay in latin digits and the dates do not, which is the rule <see cref="PersianDigits"/>
/// spells out: a byte size is an LTR technical readout, a date in Persian prose is prose.
/// </summary>
public static class DisplayFormats
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static readonly PersianCalendar Jalali = new();

    private static readonly TimeZoneInfo DisplayZone = ResolveDisplayZone();

    public static string Bytes(long value)
    {
        if (value < 1024) return string.Create(CultureInfo.InvariantCulture, $"{value} B");

        double scaled = value;
        var unit = 0;
        while (scaled >= 1024 && unit < Units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        // One decimal below 100 and none above, which is what the comp draws: 18.4 MB, 4.7 GB,
        // 214 GB. "0.#" also drops a pointless ".0" from a round number.
        var format = scaled < 100 ? "0.#" : "0";
        return $"{scaled.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>The extension, upper-cased — the comp's «نوع: PDF». Falls back to the mime subtype.</summary>
    public static string FileKind(string fileName, string mimeType)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Length > 1) return extension[1..].ToUpperInvariant();

        var slash = mimeType.LastIndexOf('/');
        return slash >= 0 && slash < mimeType.Length - 1
            ? mimeType[(slash + 1)..].ToUpperInvariant()
            : "FILE";
    }

    public static string PersianDate(DateTimeOffset value)
    {
        var local = ToDisplayZone(value);
        return PersianDigits.Translate(string.Create(
            CultureInfo.InvariantCulture,
            $"{Jalali.GetYear(local):0000}/{Jalali.GetMonth(local):00}/{Jalali.GetDayOfMonth(local):00}"));
    }

    public static string PersianDateTime(DateTimeOffset value)
    {
        var local = ToDisplayZone(value);
        return PersianDigits.Translate(string.Create(
            CultureInfo.InvariantCulture,
            $"{Jalali.GetYear(local):0000}/{Jalali.GetMonth(local):00}/{Jalali.GetDayOfMonth(local):00} — {local:HH\\:mm}"));
    }

    public static string IsoDate(DateTimeOffset value) =>
        ToDisplayZone(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The table's «تغییر» column: «امروز ۱۰:۲۲» · «دیروز» · «۳ روز پیش».</summary>
    public static string RelativeFa(DateTimeOffset value, DateTimeOffset now)
    {
        var moment = ToDisplayZone(value).Date;
        var today = ToDisplayZone(now).Date;
        var days = (today - moment).Days;

        return days switch
        {
            <= 0 => PersianDigits.Translate(string.Create(CultureInfo.InvariantCulture, $"امروز {ToDisplayZone(value):HH\\:mm}")),
            1 => "دیروز",
            < 7 => $"{PersianDigits.Plain(days)} روز پیش",
            < 35 => $"{PersianDigits.Plain(days / 7)} هفته پیش",
            _ => PersianDate(value),
        };
    }

    /// <summary>Whole days left, floored, or null when the link never expires.</summary>
    public static int? DaysUntil(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is not { } expiry) return null;
        var remaining = expiry - now;
        return remaining <= TimeSpan.Zero ? 0 : (int)remaining.TotalDays;
    }

    private static DateTime ToDisplayZone(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, DisplayZone).DateTime;

    private static TimeZoneInfo ResolveDisplayZone()
    {
        // The readers are in Tehran and the server is in Germany: formatting in the server's zone
        // puts «دیروز» on a file uploaded this morning. Both id spellings are tried because a
        // container without tzdata has neither, and a missing timezone must not take the panel down.
        string[] candidates = ["Asia/Tehran", "Iran Standard Time"];
        foreach (var id in candidates)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
