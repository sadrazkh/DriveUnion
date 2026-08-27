using System.Globalization;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// When the monthly traffic counter starts again.
///
/// <para>Three surfaces refuse a workspace that is over its allowance — the public card, the JSON
/// API and the S3 gateway — and each of them owes the caller a <c>Retry-After</c>. That is one fact
/// about one counter, so it is spelled once: three copies of a date calculation is three chances for
/// one of them to promise a moment the meter disagrees with.</para>
/// </summary>
public static class EgressWindow
{
    /// <summary>
    /// Midnight UTC on the first of the month after <paramref name="now"/>.
    ///
    /// <para>UTC because that is the clock <c>TenantUsageDay.Day</c> is stamped in, and
    /// <c>ITrafficMeter.MonthAsync</c> reads its window from those rows. A rollover computed in the
    /// reader's own zone would promise a return three and a half hours before the counter it is
    /// about actually resets.</para>
    /// </summary>
    public static DateTimeOffset NextReset(DateTimeOffset now) =>
        new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

    /// <summary>
    /// The same instant as an HTTP-date, which is the form <c>Retry-After</c> takes when it names a
    /// moment rather than a number of seconds.
    ///
    /// <para>A date rather than a delay deliberately: for a calendar allowance the server knows the
    /// exact moment the refusal lifts, and a rounded «try in 172800 seconds» is a worse answer that
    /// also goes stale the instant it is cached.</para>
    /// </summary>
    public static string NextResetHeader() =>
        NextReset(DateTimeOffset.UtcNow).ToString("R", CultureInfo.InvariantCulture);
}
