using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// Where a person's files live, resolved once and then remembered.
///
/// <para>Every level is found-or-created from the top down — <c>DriveUnion/</c> in the account, the
/// tenant's slug inside it, the person inside that, <c>.trash/</c> beside them — and every level is
/// its own cache entry. The second person to upload in a tenant that has already been used therefore
/// costs one request rather than three, and the second upload by either of them costs none.</para>
///
/// <para>Nothing here takes a folder from a caller. Everything is derived from an account, a tenant
/// and a user id, because a folder id arriving from outside addresses another customer's files
/// exactly as well as the caller's own.</para>
///
/// <para>Scoped: it reads the tenant's slug and the account's remembered root through the request's
/// <see cref="DriveUnionDbContext"/>. The cache it writes into is the singleton.</para>
/// </summary>
public sealed class DriveFolders(
    DriveUnionDbContext db,
    IDriveClient drive,
    DriveFolderCache cache) : IDriveFolders
{
    /// <summary>The <c>DriveUnion/</c> folder each account gets; tenants are folders inside it.</summary>
    private const string RootFolderName = "DriveUnion";

    /// <summary>
    /// The trash beside a home. The leading dot says whose it is: the customer's own trash is a
    /// screen built from rows, and this name is only ever read by an operator looking at Drive.
    /// </summary>
    private const string TrashFolderName = ".trash";

    public Task<string> HomeAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken)
    {
        // No person, no folder of their own. The tenant folder is where every file in this product
        // sat before uploads were separated per user, and it is still where a caller without one
        // lands — inventing a user here would put files somewhere nobody asked for.
        if (ownerUserId is not { } owner)
        {
            return TenantFolderAsync(accountId, tenantId, cancellationToken);
        }

        return cache.GetOrResolveAsync(
            new DriveFolderKey(accountId, tenantId, owner, DriveFolderKind.Home),
            async token =>
            {
                var tenantFolderId = await TenantFolderAsync(accountId, tenantId, token);

                return await drive.EnsureFolderAsync(
                    accountId, UserFolderName(owner), tenantFolderId, token);
            },
            cancellationToken);
    }

    public Task<string> TrashAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken) =>
        cache.GetOrResolveAsync(
            new DriveFolderKey(accountId, tenantId, ownerUserId, DriveFolderKind.Trash),
            async token =>
            {
                // The gates nest in one direction only — trash waits on home, home on the tenant,
                // the tenant on the root — so no two of them can ever be waiting on each other.
                var homeId = await HomeAsync(accountId, tenantId, ownerUserId, token);

                return await drive.EnsureFolderAsync(accountId, TrashFolderName, homeId, token);
            },
            cancellationToken);

    /// <summary>
    /// The folder for one person: <c>u-</c> and their user id, hyphens stripped.
    ///
    /// <para>The id, and not a display name or an address, because this is a path with files in it
    /// and the name has to mean the same person for the life of the account. A display name is
    /// neither unique inside a tenant nor fixed, and on the day somebody edits theirs the resolver
    /// finds nothing under the new name and creates a second home beside the first — half their
    /// files in each, and not one error anywhere. An address moves for the same reasons, and it also
    /// has to survive being escaped into the <c>name = '…'</c> query the Drive client builds; 32 hex
    /// characters cannot contain a quote.</para>
    ///
    /// <para>The tenant level is named by its slug instead, which is safe for the opposite reason:
    /// a slug is chosen once at provisioning, is unique across the product, and nothing updates
    /// it.</para>
    ///
    /// <para>What this costs is an operator reading an id rather than a name when they open Drive
    /// itself. That is one lookup in the panel, which is where names belong — and it is the right
    /// direction to be wrong in, because the readable alternative loses track of files quietly.</para>
    /// </summary>
    private static string UserFolderName(Guid ownerUserId) => $"u-{ownerUserId:N}";

    private Task<string> TenantFolderAsync(
        Guid accountId,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        cache.GetOrResolveAsync(
            new DriveFolderKey(accountId, tenantId, null, DriveFolderKind.Tenant),
            async token =>
            {
                var slug = await db.Tenants
                    .AsNoTracking()
                    .Where(t => t.Id == tenantId)
                    .Select(t => t.Slug)
                    .FirstOrDefaultAsync(token)
                    ?? throw new InvalidOperationException($"Tenant {tenantId} does not exist.");

                var rootFolderId = await RootFolderAsync(accountId, token);

                return await drive.EnsureFolderAsync(accountId, slug, rootFolderId, token);
            },
            cancellationToken);

    /// <summary>
    /// <c>DriveUnion/</c> in one account, from the account's own row when it has been resolved
    /// before. The row is the half of this cache that survives a restart, which is why it is worth
    /// writing at all.
    /// </summary>
    private Task<string> RootFolderAsync(Guid accountId, CancellationToken cancellationToken) =>
        cache.GetOrResolveAsync(
            new DriveFolderKey(accountId, Guid.Empty, null, DriveFolderKind.Root),
            async token =>
            {
                var recorded = await db.GoogleAccounts
                    .AsNoTracking()
                    .Where(a => a.Id == accountId)
                    .Select(a => a.RootFolderId)
                    .FirstOrDefaultAsync(token);

                if (!string.IsNullOrEmpty(recorded)) return recorded;

                var created = await drive.EnsureFolderAsync(accountId, RootFolderName, null, token);

                // One column, written straight to the row rather than through the change tracker.
                // This runs in the middle of somebody else's unit of work — an upload that has
                // already reserved bytes and has not yet written its session — and a SaveChanges
                // here would commit whatever that caller was still holding.
                await db.GoogleAccounts
                    .Where(a => a.Id == accountId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.RootFolderId, created), token);

                return created;
            },
            cancellationToken);
}
