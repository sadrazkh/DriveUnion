using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// The two dashboards behind <c>/</c>: the customer's own figures, and the operator's pool.
    ///
    /// <para>Scoped, because both share the request's <c>DriveUnionDbContext</c>.</para>
    ///
    /// <para><b>Order matters.</b> The customer's reader is built on <c>ITenantPlanService</c> and
    /// <c>ITrash</c> — the same two the sidebar's capacity card reads — and the operator's on
    /// <c>IGoogleAccountDirectory</c>, so this line has to come after <c>AddDriveUnionPlans</c>,
    /// <c>AddDriveUnionTrash</c> and <c>AddGoogleDrive</c>. A dashboard built from services that were
    /// never registered fails on the panel's home page rather than at start-up, which is the worst
    /// place in the product to discover a missing line.</para>
    ///
    /// <para>It is a call rather than three registrations in <c>Program.cs</c> for the reason
    /// <c>AddDriveUnionPlans</c> is: the test harness calls this exact method, so what the suite
    /// proves is what the application does.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionDashboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ICustomerDashboard, CustomerDashboardReader>();
        services.TryAddScoped<IOperatorDashboard, OperatorDashboardReader>();

        return services;
    }
}
