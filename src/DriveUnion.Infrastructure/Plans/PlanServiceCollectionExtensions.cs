using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Plans;

public static class PlanServiceCollectionExtensions
{
    /// <summary>
    /// The plan catalogue, the tenant's effective limits, and the operator's cross-tenant view.
    ///
    /// <para>Scoped, because it all shares the request's <c>DriveUnionDbContext</c>.
    /// <c>TenantStorageMeter</c> is deliberately absent: it is the single writer of
    /// <c>Tenant.StorageUsedBytes</c> and is static precisely so nothing can be registered in its
    /// place.</para>
    ///
    /// <para><c>ValidateOnStart</c> on purpose, for the half of the setting that can be checked
    /// without a database: <c>Plans:DefaultPlanCode</c> decides what every new customer gets, and an
    /// empty one discovered on the first sign-up is discovered from a customer. Whether the code
    /// names a real row is left to the command that uses it, because a start-up check would need a
    /// connection and a panel that refuses to boot while the database is briefly away is the worse
    /// failure.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionPlans(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<PlansOptions>()
            .BindConfiguration(PlansOptions.SectionName)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.DefaultPlanCode),
                "Plans:DefaultPlanCode must name a plan; it is what every new workspace is created with.")
            .ValidateOnStart();

        services.TryAddScoped<ITenantPlanService, TenantPlanService>();
        services.TryAddScoped<IPlanCatalogueReader, PlanCatalogueReader>();
        services.TryAddScoped<IOperatorPlanReader, OperatorPlanReader>();

        return services;
    }
}
