namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// The two numbers the sweeper runs on. Both are deployment knobs rather than operator settings:
/// they say how hard this deployment may work, not how long a customer's file is kept — that is
/// <c>OperatorSettings.TrashRetentionDays</c>, and it lives in a table because an operator changes
/// it by pressing something.
/// </summary>
public sealed class TrashOptions
{
    public const string SectionName = "Trash";

    /// <summary>
    /// How often the sweeper looks. Five minutes, because nothing here is urgent: every row it takes
    /// has already waited out a retention window measured in days, and a deployment that swept every
    /// few seconds would be asking the database for an empty answer all day.
    /// </summary>
    public int PurgeIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// How many files one sweep may destroy.
    ///
    /// <para>This is the whole reason <c>ITrashPurge</c> is bounded. A purge deletes in Drive one
    /// file at a time against the same 12,000-per-minute budget every upload in the product is
    /// sharing, so an unbounded sweep after a customer emptied a large account would spend the
    /// allowance on housekeeping while other customers are trying to upload. What is left over is
    /// still due five minutes later.</para>
    /// </summary>
    public int PurgeBatchSize { get; set; } = 50;
}
