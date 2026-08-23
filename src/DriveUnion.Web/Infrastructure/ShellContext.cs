using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// The handful of values the panel shell draws that no single page owns: the account summary under
/// the brand, the daily-upload quota card, and the signed-in user.
///
/// A page supplies it with <c>ViewData[ShellContext.Key] = new ShellContext { … }</c>. Every
/// property is nullable and the layout renders a skeleton where a value is missing, so a page that
/// has not been taught about the shell yet renders a loading sidebar rather than invented numbers.
/// Fabricated-looking figures in a chrome nobody re-reads are how a mock ships to production.
///
/// <see cref="AccountSummary"/> and the two quota figures describe the operator's pool, which by
/// §1.4 of the M1 design a customer must never see — not the numbers and not the fact that a pool
/// exists. There is deliberately no <c>IsOperator</c> flag here to carry that decision: the layout
/// asks the principal, which is the same claim <c>DriveUnionPolicies.Operator</c> authorises on. A
/// flag would be a second copy of that fact, settable per page, and a page that set it wrongly
/// would leak the pool while every test and every policy still passed.
/// </summary>
public sealed class ShellContext
{
    public const string Key = "Shell";

    /// <summary>Monospace line under the brand, e.g. "2 accounts · 10 TB". Latin by design.</summary>
    public string? AccountSummary { get; init; }

    public long? DailyQuotaUsedGb { get; init; }

    public long? DailyQuotaLimitGb { get; init; }

    public string? UserName { get; init; }

    public string? UserRole { get; init; }

    public bool HasQuota => DailyQuotaUsedGb is not null && DailyQuotaLimitGb is > 0;

    /// <summary>Bar width, clamped — a quota reported over 100% must not overflow the track.</summary>
    public double QuotaPercent => HasQuota
        ? Math.Clamp(DailyQuotaUsedGb!.Value * 100d / DailyQuotaLimitGb!.Value, 0d, 100d)
        : 0d;

    /// <summary>
    /// The handoff's rule, in one place: a daily quota at or past 80% turns amber and at or past
    /// 95% turns red. It is a rule about running out of upload allowance today, so it belongs to
    /// the value, not to whichever bar happens to draw it.
    /// </summary>
    public string QuotaFillClass => QuotaPercent switch
    {
        >= 95d => "bar-fill bar-fill--danger",
        >= 80d => "bar-fill bar-fill--warn",
        _ => "bar-fill",
    };

    public static ShellContext From(ViewDataDictionary viewData) =>
        viewData[Key] as ShellContext ?? new ShellContext();
}
