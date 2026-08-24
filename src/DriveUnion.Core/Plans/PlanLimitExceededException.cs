using DriveUnion.Core.Abstractions;

namespace DriveUnion.Core.Plans;

/// <summary>The dimension that refused. One value per row of §4's enforcement table.</summary>
public enum PlanLimit
{
    /// <summary>Stored bytes against <c>Tenant.StorageQuotaBytes</c>. M5 §7, unchanged.</summary>
    Storage,

    /// <summary>One file against <c>Tenant.MaxFileBytes</c>.</summary>
    File,

    /// <summary>Egress this window against <c>Tenant.MonthlyEgressBytes</c>. Metered by P2.</summary>
    Traffic,

    /// <summary>Seats against <c>Tenant.MaxMembers</c>. Enforced by P3.</summary>
    Members,
}

/// <summary>
/// The wire names of the four refusals, and the one place they are spelled.
///
/// <para>They are constants rather than literals at the throw sites because the same four strings
/// appear in an API body, in a log line and in a test, and a fifth spelling of one of them is a
/// client that silently stops recognising the refusal it was written for.</para>
///
/// <para><b>409, not 402.</b> <c>Payment Required</c> is the tempting one and is wrong twice: it
/// asserts the fix is money when the fix may be waiting or deleting, and this product does not do
/// money at all — a status code that announces a bill exists would be a lie the API tells before the
/// product does. Not 429 either, because nothing here is a rate; and not 507, which is a 5xx that
/// proxies and generic client code will retry, reasonably reading it as our fault.</para>
/// </summary>
public static class PlanLimitCodes
{
    public const string Storage = "tenant_quota_exceeded";

    public const string File = "file_too_large_for_plan";

    public const string Traffic = "tenant_traffic_exceeded";

    public const string Members = "member_limit_reached";

    /// <summary>The <c>limit</c> field of the body, which is what tells a client which one fired.</summary>
    public static string Dimension(PlanLimit limit) => limit switch
    {
        PlanLimit.Storage => "storage",
        PlanLimit.File => "file",
        PlanLimit.Traffic => "traffic",
        PlanLimit.Members => "members",
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    public static string For(PlanLimit limit) => limit switch
    {
        PlanLimit.Storage => Storage,
        PlanLimit.File => File,
        PlanLimit.Traffic => Traffic,
        PlanLimit.Members => Members,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };
}

/// <summary>
/// A plan said no. Not a storage fault, not a full pool, not a busy Google — the customer's own
/// ceiling, which is a fact about their account and is true whichever way the file arrived.
///
/// <para><b>Why it is an exception at all.</b> <c>IUploadCoordinator.BeginAsync</c> returns a
/// non-nullable result, so a refusal has to be one. That is the same reason
/// <c>UploadRejectedException</c> is one.</para>
///
/// <para><b>Why it derives from <see cref="DriveApiException"/>, and what that costs today.</b> It is
/// deliberately <i>not</i> a Drive failure, and its name says so. It derives from that base only so a
/// caller which already catches trouble on the upload path catches this too rather than letting a
/// customer's own quota escape as an unhandled 500. Until
/// <c>DriveApiExceptionFilterAttribute</c> gains a case for it, the API answers this with the filter's
/// default — which is wrong and is the one piece of wiring P1 could not make, because that file
/// belongs to another slice. The <see cref="Code"/>, <see cref="Limit"/> and the three figures below
/// are everything that case needs.</para>
/// </summary>
public sealed class PlanLimitExceededException : DriveApiException
{
    private PlanLimitExceededException(
        string message,
        PlanLimit limit,
        long requestedBytes,
        long usedBytes,
        long capBytes)
        : base(message)
    {
        Limit = limit;
        RequestedBytes = requestedBytes;
        UsedBytes = usedBytes;
        CapBytes = capBytes;
    }

    public PlanLimit Limit { get; }

    /// <summary>The wire name, e.g. <c>file_too_large_for_plan</c>.</summary>
    public string Code => PlanLimitCodes.For(Limit);

    /// <summary>What this request asked for.</summary>
    public long RequestedBytes { get; }

    /// <summary>What was already spent. Zero for a per-file refusal, which spends nothing.</summary>
    public long UsedBytes { get; }

    /// <summary>The ceiling that refused.</summary>
    public long CapBytes { get; }

    /// <summary>
    /// One file, larger than the plan allows. No path in the product accepts it, which is why the
    /// customer-facing sentence for this one carries no link to an uploader — sending them to a
    /// second uploader that refuses the same file again is a dead end.
    /// </summary>
    public static PlanLimitExceededException File(long requestedBytes, long maxFileBytes) =>
        new(
            $"A file of {requestedBytes} bytes is over this tenant's per-file limit of {maxFileBytes}.",
            PlanLimit.File,
            requestedBytes,
            usedBytes: 0,
            capBytes: maxFileBytes);

    /// <summary>
    /// The tenant has no room. The way out is deleting files, which needs the panel, which keeps
    /// working: M5 §7's over-cap tenant loses uploads and nothing else.
    /// </summary>
    public static PlanLimitExceededException Storage(long requestedBytes, long usedBytes, long capBytes) =>
        new(
            $"A file of {requestedBytes} bytes does not fit in this tenant's remaining "
            + $"{Math.Max(0, capBytes - usedBytes)} bytes of {capBytes}.",
            PlanLimit.Storage,
            requestedBytes,
            usedBytes,
            capBytes);
}
