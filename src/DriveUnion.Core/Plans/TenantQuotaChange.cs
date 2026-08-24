namespace DriveUnion.Core.Plans;

/// <summary>
/// Which of a tenant's four effective limits a change moved.
///
/// <para>Persisted, so the values are explicit and gapped — a fifth dimension can be inserted without
/// renumbering stored rows, and declaration order must never decide what is in a column.</para>
/// </summary>
public enum QuotaField : byte
{
    StorageBytes = 10,
    MaxFileBytes = 20,
    MonthlyEgressBytes = 30,
    MaxMembers = 40,
}

/// <summary>
/// One field of one tenant's effective limits, moved by somebody, for a stated reason.
///
/// <para>This is the row that answers «چرا سهمیه‌ام عوض شد» without a support engineer writing SQL,
/// and it is the reason a plan is copied rather than joined: a template edit cannot produce a per
/// tenant history, and a cap is a promise to one customer.</para>
///
/// <para>It is a deliberate, narrow tension with M5 §12, which declined an audit-log screen. This is
/// not one: it is one table on one page. The general audit log stays declined.</para>
/// </summary>
public sealed class TenantQuotaChange
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>
    /// The operator who did it, or null when nobody did.
    ///
    /// <para>Nullable because the two writers that have no person behind them are real: the numbers a
    /// tenant is created with, and a plan applied by configuration rather than by a click. Inventing
    /// a "system" GUID to keep the column non-nullable would put a plausible-looking identifier in an
    /// audit trail that is only worth having because everything in it is true.</para>
    /// </summary>
    public Guid? ChangedByUserId { get; set; }

    /// <summary>The plan the tenant was on, or null when it was on none.</summary>
    public string? PlanCodeBefore { get; set; }

    /// <summary>
    /// The plan the tenant is on after the change. Equal to <see cref="PlanCodeBefore"/> for an
    /// override, which is the point: an override does not take the tenant off its plan.
    /// </summary>
    public string? PlanCodeAfter { get; set; }

    public QuotaField Field { get; set; }

    public long OldValue { get; set; }

    public long NewValue { get; set; }

    /// <summary>
    /// Why. Required, because a quota change with no reason is the one a support conversation cannot
    /// use and the whole table exists for support conversations.
    /// </summary>
    public required string Reason { get; set; }
}
