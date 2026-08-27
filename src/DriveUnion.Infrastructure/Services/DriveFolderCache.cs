using System.Collections.Concurrent;

namespace DriveUnion.Infrastructure.Services;

/// <summary>Which of the folders in the layout a key names.</summary>
public enum DriveFolderKind
{
    /// <summary><c>DriveUnion/</c>, one per Google account and belonging to no tenant.</summary>
    Root,

    /// <summary><c>DriveUnion/{tenant}/</c>.</summary>
    Tenant,

    /// <summary><c>DriveUnion/{tenant}/{user}/</c>.</summary>
    Home,

    /// <summary>The <c>.trash/</c> beside that home.</summary>
    Trash,

    /// <summary>
    /// <c>DriveUnion/.catalogue/</c> — the operator's own, holding the snapshots of the database
    /// that says whose every other file is.
    ///
    /// <para>A sibling of the tenant folders and inside none of them, which is not tidiness: a
    /// customer's tree is a place their files are restored into and their trash is emptied from,
    /// and the index of the whole product must not be reachable by anything that walks one.</para>
    /// </summary>
    Catalogue,
}

/// <summary>
/// What identifies one folder.
///
/// <para><see cref="TenantId"/> is <see cref="Guid.Empty"/> on a <see cref="DriveFolderKind.Root"/>
/// key, which belongs to the account rather than to anybody's tenant, and
/// <see cref="OwnerUserId"/> is null at every level above a person.</para>
/// </summary>
public readonly record struct DriveFolderKey(
    Guid AccountId,
    Guid TenantId,
    Guid? OwnerUserId,
    DriveFolderKind Kind);

/// <summary>
/// The folder ids this process has already resolved, and the gate that stops two callers resolving
/// the same one twice.
///
/// <para>A resolve is a Drive list and possibly a create — two requests against a budget of 12,000 a
/// minute shared with every upload in the product — to learn a fact that does not change. A Drive
/// folder id is fixed for the life of the folder: renaming it, or dragging it somewhere else in the
/// account, leaves the id alone. That is what makes a cache with no expiry the right shape rather
/// than a shortcut.</para>
///
/// <para><b>Nothing invalidates an entry, and nothing has to.</b> No path in this product deletes a
/// folder — the trash moves files into one, and the purge deletes files and never their parent — so
/// the only way a cached id goes stale is an operator removing the folder in Drive by hand. An
/// upload into a folder that is gone fails at Google, loudly, instead of landing somewhere
/// unexpected, and the next process to start resolves it again. If something here ever does delete a
/// folder, it has to clear the entries that named it; it will be the first thing that needs to.</para>
///
/// <para><b>One gate per key, held across the whole round trip.</b>
/// <c>IDriveClient.EnsureFolderAsync</c> is a find and then a create, which is two requests with a
/// gap between them: two uploads arriving together for a person who has never uploaded both find
/// nothing and both create, and that person ends up with two folders of the same name holding half
/// their files each. Drive is perfectly happy to hold both — names are not unique there — so nothing
/// downstream would ever report it. The gate is held until there is an answer, not just around the
/// write, because whoever is waiting wants the folder and not a turn at making one.</para>
///
/// <para><b>Two panel instances are the same race one process wider, and this does not close it.</b>
/// No lock reaches into another process. What bounds it is that the window is a single round trip,
/// that it is only ever the first upload of a given person into a given account, and that nothing in
/// this product derives a file's location afterwards — every row records the folder its file went
/// into, so a duplicated folder costs a tidy-up and never a lost file. Closing it for real means the
/// folder id in a row under a unique index on account, tenant and user; that is a migration, and
/// this phase already has the only one it is allowed.</para>
///
/// <para>A gate rather than one shared task handed to every waiter: each caller resolves through its
/// own request's <see cref="Persistence.DriveUnionDbContext"/> and its own Drive client, so a caller
/// that gives up cannot take a resolution somebody else is waiting on down with it.</para>
///
/// <para>A resolve that throws writes nothing, so a rate limit or an expired token leaves the cache
/// as it was rather than pinning the failure for the life of the process.</para>
///
/// <para>Registered as a singleton — the resolver reading it is scoped, because it shares the
/// request's database context. Both dictionaries hold one small entry per account-tenant-user
/// combination that has actually uploaded since the process started, and neither shrinks, which is
/// the same thing as saying the answers are facts.</para>
/// </summary>
public sealed class DriveFolderCache
{
    private readonly ConcurrentDictionary<DriveFolderKey, string> _resolved = new();
    private readonly ConcurrentDictionary<DriveFolderKey, SemaphoreSlim> _gates = new();

    /// <summary>
    /// The id for <paramref name="key"/>, resolving it exactly once per process.
    ///
    /// <para><paramref name="resolve"/> receives the caller's own cancellation token: it runs on the
    /// caller's dependencies, so it is the caller's to abandon.</para>
    /// </summary>
    public async Task<string> GetOrResolveAsync(
        DriveFolderKey key,
        Func<CancellationToken, Task<string>> resolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        if (_resolved.TryGetValue(key, out var known)) return known;

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            // The second look. Everybody who queued behind the first caller is here to collect the
            // answer that caller produced, and this is the line that makes them do so.
            if (_resolved.TryGetValue(key, out known)) return known;

            var folderId = await resolve(cancellationToken);

            _resolved[key] = folderId;

            return folderId;
        }
        finally
        {
            gate.Release();
        }
    }
}
