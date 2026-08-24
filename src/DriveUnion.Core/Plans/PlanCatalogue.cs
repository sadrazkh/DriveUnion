namespace DriveUnion.Core.Plans;

/// <summary>
/// The tiers the product ships with, and the single place their numbers are written.
///
/// <para><b>Every figure below is a placeholder and the operator's screen says so in words.</b> §15.2
/// of the plans-and-quotas design leaves the tier names and all four numbers per tier to the owner,
/// and the owner has not answered. What is not a placeholder is the <i>shape</i>: three tiers, four
/// dimensions, an entry tier a stranger can be given, and per-file kept far below monthly traffic.</para>
///
/// <para><b>Why the numbers are the ones below.</b> Three constraints from §15.2 were used, and each
/// is worth reading before any of them is changed:</para>
/// <list type="number">
/// <item><b>«دو گیگ» is read as a per-file limit</b>, per §15.2's third bullet: the owner's message is
/// about «حجم هر فایل», and 2 GB would be an absurd storage cap for a product whose upload path is
/// built for 96 GB files. It is applied to <see cref="Standard"/> — the tier a paying customer is
/// most likely to be on — rather than to the entry tier.</item>
/// <item><b>Per-file is the error bar on the traffic limit</b> (§4.3b), so a tier whose per-file
/// number is a large fraction of its monthly traffic will visibly overshoot and a customer will
/// screenshot it. Every tier below keeps per-file under half a percent of monthly traffic.</item>
/// <item><b>The entry tier's per-file limit sits below production's
/// <c>Telegram:MaxReceiveBytes</c></b> (2,000,000,000 bytes since the bot moved to a self-hosted Bot
/// API server), so that §4.2's ordering — the plan's refusal is the one that actually fires, and it
/// carries no link to an uploader that would refuse the same file again — is reachable on the tier
/// most customers are on. It is <i>not</i> true of <see cref="Business"/>, whose 8 GiB per-file limit
/// is above that ceiling; there Telegram refuses first, honestly, and its message's uploader link is
/// the right next step.</item>
/// </list>
///
/// <para>Storage deliberately over-commits the pool, which M5 §7 decided is allowed and shown rather
/// than prevented: about a hundred <see cref="Starter"/> tenants fill a 10 TB pool on paper, and the
/// operator's screen renders the commitment against the pool rather than refusing the sign-up.</para>
///
/// <para><b>Binary units, not decimal.</b> <c>DisplayFormats.Bytes</c> divides by 1024, so a binary
/// figure is what renders as a round «۱۰۰ GB» in the panel. A decimal 100,000,000,000 would render as
/// «93.1 GB», which is the kind of number a customer opens a ticket about.</para>
/// </summary>
public static class PlanCatalogue
{
    private const long Gib = 1024L * 1024 * 1024;
    private const long Tib = 1024L * Gib;

    /// <summary>
    /// Deterministic and obviously synthetic. <c>HasData</c> needs literal keys, and a key that
    /// looked like a real generated GUID would invite somebody to believe it meant something.
    /// </summary>
    public static readonly Guid StarterId = new("10000000-0000-4000-8000-000000000001");

    public static readonly Guid StandardId = new("10000000-0000-4000-8000-000000000002");

    public static readonly Guid BusinessId = new("10000000-0000-4000-8000-000000000003");

    /// <summary>The moment the seeded rows claim to have been created. Fixed, so the seed is stable.</summary>
    public static readonly DateTimeOffset SeededAt = DateTimeOffset.UnixEpoch;

    public const string StarterCode = "starter";

    public const string StandardCode = "standard";

    public const string BusinessCode = "business";

    /// <summary>
    /// The tier a tenant gets when nothing else has said otherwise, and the value
    /// <c>Plans:DefaultPlanCode</c> defaults to.
    ///
    /// <para>100 GiB of storage, 1 GiB per file, 300 GiB of traffic a month, 3 seats. The per-file
    /// number is half of the owner's «دو گیگ» on purpose — see the class remarks, constraint 3.</para>
    /// </summary>
    public static readonly PlanNumbers Starter = new(
        StorageBytes: 100 * Gib,
        MaxFileBytes: 1 * Gib,
        MonthlyEgressBytes: 300 * Gib,
        MaxMembers: 3);

    /// <summary>500 GiB, the owner's «دو گیگ» per file, 1.5 TiB of traffic, 10 seats.</summary>
    public static readonly PlanNumbers Standard = new(
        StorageBytes: 500 * Gib,
        MaxFileBytes: 2 * Gib,
        MonthlyEgressBytes: 1536 * Gib,
        MaxMembers: 10);

    /// <summary>2 TiB, 8 GiB per file, 6 TiB of traffic, 25 seats.</summary>
    public static readonly PlanNumbers Business = new(
        StorageBytes: 2 * Tib,
        MaxFileBytes: 8 * Gib,
        MonthlyEgressBytes: 6 * Tib,
        MaxMembers: 25);

    /// <summary>
    /// The code a deployment that has configured nothing lands on, and the numbers a <c>Tenant</c>
    /// row carries before anything has applied a plan to it.
    ///
    /// <para>The default is the <i>smallest</i> tier, which is the direction a default has to fail
    /// in: §3 refuses a nullable cap meaning "unlimited" precisely because it is one migration
    /// default away from every tenant being uncapped and nothing looking wrong until the pool is
    /// full. A generous default has the same shape.</para>
    /// </summary>
    public const string DefaultCode = StarterCode;

    /// <inheritdoc cref="DefaultCode"/>
    public static PlanNumbers Default => Starter;

    /// <summary>
    /// The seeded rows, in the order the operator's list draws them.
    ///
    /// Returned as a projection rather than as cached <see cref="Plan"/> instances so that a caller
    /// which mutates what it is given cannot change what the next caller sees.
    /// </summary>
    public static IReadOnlyList<Plan> Seed() =>
    [
        Row(StarterId, StarterCode, "پایه", Starter, 10),
        Row(StandardId, StandardCode, "استاندارد", Standard, 20),
        Row(BusinessId, BusinessCode, "تجاری", Business, 30),
    ];

    /// <summary>
    /// <c>Plan.Name</c> is a database row an operator renames, not a catalogue entry, so it is not in
    /// <c>UiText</c> and it is seeded in one language. The screen renders whatever the row says.
    /// </summary>
    private static Plan Row(Guid id, string code, string name, PlanNumbers numbers, int sortOrder) => new()
    {
        Id = id,
        Code = code,
        Name = name,
        StorageBytes = numbers.StorageBytes,
        MaxFileBytes = numbers.MaxFileBytes,
        MonthlyEgressBytes = numbers.MonthlyEgressBytes,
        MaxMembers = numbers.MaxMembers,
        IsRetired = false,
        SortOrder = sortOrder,
        CreatedAt = SeededAt,
    };
}
