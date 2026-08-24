using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Trash;

public static class TrashServiceCollectionExtensions
{
    /// <summary>
    /// The trash: the move a delete makes, the customer's list, restore and empty, the sweeper's
    /// bounded purge, and the operator settings row the retention window comes from.
    ///
    /// <para>Scoped, because it all shares the request's <c>DriveUnionDbContext</c>.
    /// <c>TenantStorageMeter</c> is deliberately absent, as it is from <c>AddDriveUnionPlans</c>: it
    /// is the single writer of <c>Tenant.StorageUsedBytes</c> and is static precisely so nothing can
    /// be registered in its place.</para>
    ///
    /// <para><see cref="ITrashMover"/> is what makes <c>FileCatalog.DeleteAsync</c> move bytes
    /// instead of only stamping a column. Without this call the catalogue still soft-deletes and
    /// still revokes links — it simply has no trash to move anything into.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTrash(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<TrashOptions>().BindConfiguration(TrashOptions.SectionName);

        services.TryAddScoped<IOperatorSettingsStore, OperatorSettingsStore>();
        services.TryAddScoped<ITrashMover, TrashMover>();
        services.TryAddScoped<ITrash, TrashService>();
        services.TryAddScoped<ITrashPurge, TrashPurge>();

        return services;
    }

    /// <summary>
    /// The background loop that runs the purge.
    ///
    /// <para>Separate from <see cref="AddDriveUnionTrash"/> on purpose, and for the reason
    /// <c>AddDriveUnionTelegramTransport</c> is separate from <c>AddDriveUnionTelegram</c>: every
    /// in-process test host boots the real pipeline over one shared SQLite connection, and a
    /// background loop opening scopes against it turns unrelated suites into «database is locked».
    /// Without this line nothing is ever purged and the trash grows for ever.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTrashSweeper(this IServiceCollection services)
    {
        services.AddHostedService<TrashPurgeService>();

        return services;
    }
}
