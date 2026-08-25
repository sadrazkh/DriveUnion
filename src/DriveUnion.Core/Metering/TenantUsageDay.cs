namespace DriveUnion.Core.Metering;

/// <summary>
/// One workspace's egress and downloads for one day.
///
/// <para><b>A roll-up and not an event log.</b> <c>DownloadEvent</c> already records every pull, and
/// three screens wanted to ask it «how much this month» and «how many this week» — questions it
/// cannot answer. It is indexed <c>(ShareLinkId, OccurredAt)</c>, and SQLite will neither compare
/// nor <c>ORDER BY</c> a <c>DateTimeOffset</c> in SQL, so a period could only be applied in memory:
/// by reading every download a workspace has ever served, on the panel's most-visited page. That is
/// the reason the dashboard drew a lifetime total and said so rather than inventing a window.</para>
///
/// <para><b>Why <see cref="Day"/> is a <c>DateOnly</c>.</b> It is exactly the problem above, avoided
/// rather than worked around: a date is <c>date</c> on Postgres and a sortable <c>TEXT</c> on
/// SQLite, and «the days in this month» is a range both providers compare correctly in SQL. The
/// event log keeps its timestamps and its audit job; this answers the arithmetic.</para>
///
/// <para><b>Why days and not months.</b> A month is what the plan sells, and «this month» is one
/// SUM over at most thirty-one rows. A week, a day and a chart of the last thirty are the same SUM
/// over a different range — and a month-grained row could answer none of them. Thirty-one rows per
/// workspace per month is nothing to store and is the difference between a figure and a graph.</para>
/// </summary>
public sealed class TenantUsageDay
{
    public Guid TenantId { get; set; }

    /// <summary>UTC, and the same UTC the rest of this product stamps everything in.</summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// Bytes this workspace actually put on the wire.
    ///
    /// <para>What was sent, not what was promised. A visitor who closes the tab at 90% of a 200 MB
    /// file cost the operator 180 MB of Google's egress and not 200, and a player seeking through a
    /// video pays for the ranges it asked for and nothing else — so this is counted as the body is
    /// copied rather than taken from <c>Content-Length</c>.</para>
    /// </summary>
    public long EgressBytes { get; set; }

    /// <summary>Counted downloads, matching what <c>ShareLink.DownloadCount</c> increments on.</summary>
    public int Downloads { get; set; }
}
