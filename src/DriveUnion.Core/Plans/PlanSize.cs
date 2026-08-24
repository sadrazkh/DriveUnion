namespace DriveUnion.Core.Plans;

/// <summary>
/// The unit an operator types a tier's three byte columns in, and the arithmetic that gets it into
/// and back out of the column.
///
/// <para><b>Binary, because this product already decided that.</b> <c>DisplayFormats.Bytes</c>
/// divides by 1024, and <see cref="PlanCatalogue"/> is written in multiples of 1024³. A decimal GB
/// on this one screen would make «۱۰۰ GB» typed here render as «93.1 GB» on the customer's card —
/// the kind of disagreement a customer opens a ticket about, and the reason a second convention is
/// worse than either convention alone.</para>
///
/// <para><b>One unit in, one unit out.</b> The field is GB, the read-back is GB, and nothing on the
/// way through scales to TB. That is not cosmetic: <c>DisplayFormats.Bytes(6 TiB)</c> is
/// <c>"6 TB"</c>, so a form that pre-filled a GB field from it would show <c>6</c> where the stored
/// figure is 6144, and every save would divide the tier by 1024. The auto-scaled rendering stays
/// where it belongs — on the tables and cards that only ever read.</para>
///
/// <para>Whole gigabytes only. Every seeded figure is one, including the largest tier's 6 TiB
/// (6144 GB), so a round trip is exact by construction rather than by rounding. A ceiling below
/// 1 GB is not expressible here and is not meant to be: the per-tenant override on a workspace's
/// own page takes bytes, which is where a one-off number belongs anyway.</para>
/// </summary>
public static class PlanSize
{
    /// <summary>1024³. The same divisor <c>DisplayFormats.Bytes</c> uses.</summary>
    public const long BytesPerGigabyte = 1024L * 1024 * 1024;

    /// <summary>
    /// 1 PiB, expressed in the form's own unit.
    ///
    /// <para>It is an overflow guard rather than a product limit: <c>gigabytes * 1024³</c> has to
    /// stay inside a <c>long</c>, and a typo with four extra zeros in it should be refused with a
    /// sentence rather than wrap into a negative ceiling that refuses every upload.</para>
    /// </summary>
    public const long MaxGigabytes = 1024L * 1024;

    /// <summary>The smallest tier this screen can express. Zero would refuse every upload.</summary>
    public const long MinGigabytes = 1;

    public static bool IsInRange(long gigabytes) =>
        gigabytes is >= MinGigabytes and <= MaxGigabytes;

    public static long ToBytes(long gigabytes) => gigabytes * BytesPerGigabyte;

    /// <summary>
    /// The figure the form shows for a stored column, rounded down.
    ///
    /// <para>Rounding is only reachable for a row that was not written through this screen — a
    /// direct SQL edit, or a seed somebody changed. <see cref="IsWholeGigabytes"/> is how the form
    /// tells the operator that saving would move a number they did not mean to touch.</para>
    /// </summary>
    public static long ToGigabytes(long bytes) => bytes / BytesPerGigabyte;

    public static bool IsWholeGigabytes(long bytes) => bytes % BytesPerGigabyte == 0;
}
