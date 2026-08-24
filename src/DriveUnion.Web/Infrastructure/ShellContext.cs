using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// A tenant's own capacity card, which is the same box in the same slot as the operator's quota card
/// and shares not one figure with it.
///
/// <para>The request was "like the operator". The operator's card reads the daily 750 GB each Google
/// account is allowed, which is a fact about the operator's pool and is exactly what §1.4 of the M1
/// design says a customer must never see — not the number, and not that a pool exists. What a
/// customer sees above their name is their own: storage spent against their plan's cap, the traffic
/// their plan includes this month, and what their trash is holding. So the shape of the card is
/// borrowed and the figures are not.</para>
///
/// <para>The trash's size is on it because it is precisely the difference between what a customer
/// believes they freed and what they actually did — the misunderstanding this whole phase started
/// from. It is a figure about capacity, so it belongs on the capacity card rather than only on the
/// screen somebody has to go looking for.</para>
///
/// <para>Every member is a string that has already been formatted, and that is deliberate: the
/// layout renders the shell for a signed-in customer, an operator and the anonymous sign-in page,
/// and a card that did its own arithmetic in a view would be arithmetic no test can reach. The fill
/// class is decided by whoever builds this, from the one ladder the rest of the panel uses.</para>
/// </summary>
/// <param name="StorageText">«4.7 GB / 100 GB» — already Latin, and rendered inside a dir="ltr" run.</param>
/// <param name="StoragePercent">Clamped to the track by its builder; a bar cannot render 140%.</param>
/// <param name="TrafficText">The plan's monthly allowance, with a dash where the spent figure will go.</param>
/// <param name="TrashText">What the trash is holding, as a byte quantity.</param>
public sealed record ShellCapacity(
    string StorageText,
    double StoragePercent,
    string StorageFillClass,
    string TrafficText,
    string TrashText);

/// <summary>
/// The handful of values the panel shell draws that no single page owns: the account summary under
/// the brand, the daily-upload quota card, the tenant's own capacity card, and the signed-in user.
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

    /// <summary>
    /// The signed-in customer's own figures, when the page already had them in hand.
    ///
    /// <para>Null on every page that has not, which is all of them today — so the layout asks
    /// <see cref="IShellCapacity"/> instead rather than leaving the card to appear on whichever two
    /// screens happened to be written last. A customer meets this card on every page or the number
    /// they are looking for is on none of the pages they look at.</para>
    ///
    /// <para>It is never read for an operator. The layout asks the principal which card to draw, the
    /// same claim <c>DriveUnionPolicies.Operator</c> authorises on, so a page that filled this in
    /// wrongly still could not put a customer's card where the pool's belongs, or the other way
    /// round.</para>
    /// </summary>
    public ShellCapacity? Capacity { get; init; }

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
