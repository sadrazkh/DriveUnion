using DriveUnion.Core.Application;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// The signed-in customer's own capacity figures, for the card above their name.
///
/// <para>It is a service the shell asks rather than a value each page supplies. The sidebar is drawn
/// on every screen in the panel and the controllers that draw those screens belong to other slices;
/// a card that only appeared where somebody remembered to fill <see cref="ShellContext.Capacity"/> in
/// would be missing from exactly the screens a customer is on when they wonder where their space
/// went. The operator's quota card has the same property and solves it the same way — the layout
/// asks, the page does not have to remember.</para>
///
/// <para>It costs two reads per render: one tenant row and one sum over that tenant's trashed files,
/// both already indexed. That is the same order of cost as the plan screen, which is what a figure
/// that has to be true on every page costs.</para>
/// </summary>
public interface IShellCapacity
{
    /// <summary>
    /// Null when there is no such workspace. A workspace the claim names and the database does not
    /// is a fault rather than a row of zeroes — inventing figures for it is how a broken session
    /// comes to read as a customer with an empty account.
    /// </summary>
    Task<ShellCapacity?> ReadAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IShellCapacity"/> over the two readers that already hold these numbers.
///
/// <para>Nothing here is new arithmetic. Storage and its cap come from the same tenant-scoped
/// service the customer's plan screen reads, the trash's size from <see cref="ITrash"/>, and the
/// colour of the bar from <see cref="PlanMeter"/> — so the sidebar and the plan card cannot come to
/// disagree about how full a workspace is, or about the percentage at which the bar turns amber.</para>
/// </summary>
public sealed class ShellCapacityReader(ITenantPlanService plans, ITrash trash) : IShellCapacity
{
    public async Task<ShellCapacity?> ReadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetAsync(tenantId, cancellationToken);

        if (plan is null) return null;

        var trashBytes = await trash.SizeAsync(tenantId, cancellationToken);
        var percent = PlanMeter.Percent(plan.StorageUsedBytes, plan.Limits.StorageBytes);

        return new ShellCapacity(
            UiText.Plans.OfCap(
                DisplayFormats.Bytes(plan.StorageUsedBytes),
                DisplayFormats.Bytes(plan.Limits.StorageBytes)),
            percent,
            PlanMeter.FillClass(percent),
            UiText.Capacity.TrafficOfCap(DisplayFormats.Bytes(plan.Limits.MonthlyEgressBytes)),
            DisplayFormats.Bytes(trashBytes));
    }
}

/// <summary>
/// The one line the trash panel needs from <c>Program.cs</c>:
/// <c>builder.Services.AddDriveUnionTrashPanel();</c>
///
/// <para>It is a call rather than a hand-rolled registration in <c>Program.cs</c> for the reason
/// <c>AddDriveUnionPlans</c> is: the test harness calls this exact method, so what the suite proves
/// is what the application does.</para>
///
/// <para>It registers what this slice's screens own and nothing else. <see cref="ITrash"/> belongs to
/// the service layer and is registered with the rest of that layer, so this line has to come after
/// whichever call brings it — a panel that drew a capacity card from a trash service that was never
/// registered would fail at the first page render rather than at start-up.</para>
/// </summary>
public static class TrashPanelServiceCollectionExtensions
{
    public static IServiceCollection AddDriveUnionTrashPanel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IShellCapacity, ShellCapacityReader>();

        return services;
    }
}
