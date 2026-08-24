using DriveUnion.Core.Plans;

namespace DriveUnion.Infrastructure.Plans;

/// <summary>
/// The plan settings a deployment supplies. One key today, and it is the one that decides what a
/// stranger gets.
/// </summary>
public sealed class PlansOptions
{
    public const string SectionName = "Plans";

    /// <summary>
    /// The tier a tenant is created with.
    ///
    /// <para>This <b>replaces</b> M5 §8's <c>Tenancy:DefaultStorageQuotaBytes</c> rather than sitting
    /// beside it. Two configuration keys that can disagree about what a new customer gets is a bug
    /// waiting for the day they do, and the storage number now lives in the plan's row.</para>
    ///
    /// <para>An empty value is refused at start-up, and a value that matches no row makes
    /// <c>ApplyDefaultPlanAsync</c> throw naming the code. Neither falls back to a tier of its own
    /// choosing: a silent fallback here is how every customer created during a misconfiguration ends
    /// up on a plan nobody sold them. The catalogue is not consulted at start-up because the
    /// validation would need a database connection to run, and a panel that refuses to boot when the
    /// database is briefly away is a worse failure than the one it is guarding against.</para>
    /// </summary>
    public string DefaultPlanCode { get; set; } = PlanCatalogue.DefaultCode;
}
