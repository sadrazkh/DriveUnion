using System.Security.Cryptography;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// The queue behind «افزودن از لینک»: accept a URL, watch it, stop it.
///
/// <para>Everything here is tenant-scoped by an explicit argument, like the rest of the product. A
/// fetch belongs to the workspace that asked for it and the file lands in the folder of the person
/// who did — the same two facts an ordinary upload carries, for the same reasons.</para>
/// </summary>
public sealed class RemoteFetches(
    DriveUnionDbContext db,
    ContentKeyring keyring,
    TimeProvider clock) : IRemoteFetches
{
    public async Task<RemoteFetchStartResult> StartAsync(
        Guid tenantId,
        Guid? ownerUserId,
        string url,
        FetchCustody? custody,
        byte[]? contentKey,
        CancellationToken cancellationToken)
    {
        // The two halves of a lock arrive together or not at all. One without the other is a job
        // that would either seal with a key nothing can unwrap, or store a wrapping for a key that
        // was never used — both of which produce a file nobody can open, discovered by whoever tries.
        if ((custody is null) != (contentKey is null)
            || custody?.IsWellFormed == false
            || (contentKey is not null && contentKey.Length != Du1.KeyBytes))
        {
            return new RemoteFetchStartResult(null, RemoteSourceRefusal.None, Detail: "bad_custody");
        }

        // The URL's own shape, refused now rather than by a job that fails in a minute. What it
        // resolves to is not checked here on purpose — see IRemoteFetches.
        var refusal = RemoteSource.Inspect(url, out var parsed);

        if (refusal != RemoteSourceRefusal.None || parsed is null)
        {
            return new RemoteFetchStartResult(null, refusal);
        }

        var inFlight = await db.RemoteFetches.CountAsync(
            f => f.TenantId == tenantId
                && (f.Status == RemoteFetchStatus.Queued || f.Status == RemoteFetchStatus.Running),
            cancellationToken);

        if (inFlight >= RemoteFetch.MostInFlightPerTenant)
        {
            // Not a refusal about the URL, and it is worth saying which: without a cap this feature
            // is a free bandwidth proxy, and the customer's next action is to wait rather than to
            // fix their link.
            return new RemoteFetchStartResult(null, RemoteSourceRefusal.None, Detail: "queue_full");
        }

        var fetch = new RemoteFetch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,

            // The parsed form, so what is stored and what is dialled are the same string. A URL kept
            // exactly as typed and then re-parsed later is two chances to disagree.
            Url = parsed.AbsoluteUri,
            Status = RemoteFetchStatus.Queued,
            CreatedAt = clock.GetUtcNow(),
        };

        if (custody is not null)
        {
            // Written down as it arrived. Nothing here derives anything: the browser did that, and
            // the passphrase it derived from never left it. What is stored is the wrapping; what is
            // held in memory is the raw key. See ContentKeyring for what that costs on a restart.
            fetch.KdfSalt = custody.KdfSalt;
            fetch.KdfIterations = custody.KdfIterations;
            fetch.WrappedKey = custody.WrappedKey;
            fetch.NoncePrefix = custody.NoncePrefix;
        }

        db.RemoteFetches.Add(fetch);
        await db.SaveChangesAsync(cancellationToken);

        // After the row exists, so a key is never held for a fetch that was not written.
        if (contentKey is not null) keyring.Hold(fetch.Id, contentKey);

        return new RemoteFetchStartResult(fetch.Id, RemoteSourceRefusal.None);
    }

    public async Task<IReadOnlyList<RemoteFetchView>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await db.RemoteFetches
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset and this has to behave
        // the same on it as on Postgres. See ShareLinkService.
        return
        [
            .. rows
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new RemoteFetchView(
                    f.Id,
                    f.Url,
                    f.FileName,
                    f.Status,
                    f.SizeBytes,
                    f.BytesFetched,
                    f.FailureReason,
                    f.CreatedAt)),
        ];
    }

    public async Task<bool> CancelAsync(
        Guid tenantId,
        Guid fetchId,
        CancellationToken cancellationToken)
    {
        // Both predicates: another workspace's fetch id is not found rather than found and refused.
        var fetch = await db.RemoteFetches.FirstOrDefaultAsync(
            f => f.Id == fetchId && f.TenantId == tenantId,
            cancellationToken);

        if (fetch is null) return false;

        if (fetch.Status is not (RemoteFetchStatus.Queued or RemoteFetchStatus.Running)) return false;

        fetch.Status = RemoteFetchStatus.Cancelled;
        fetch.FinishedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        // The key goes with it. A cancelled fetch will never be resumed, so holding one is holding
        // the thing that opens a file nobody is going to write.
        keyring.Release(fetchId);

        return true;
    }

    public async Task<bool> DismissAsync(
        Guid tenantId,
        Guid fetchId,
        CancellationToken cancellationToken)
    {
        // Both predicates, for the reason CancelAsync gives: another workspace's id is not found.
        var fetch = await db.RemoteFetches.FirstOrDefaultAsync(
            f => f.Id == fetchId && f.TenantId == tenantId,
            cancellationToken);

        if (fetch is null) return false;

        // Live work is not history. See IRemoteFetches.DismissAsync.
        if (fetch.Status is RemoteFetchStatus.Queued or RemoteFetchStatus.Running) return false;

        db.RemoteFetches.Remove(fetch);
        await db.SaveChangesAsync(cancellationToken);

        // Belt and braces: a completed or failed fetch has released its key already, and a row that
        // stopped some other way may not have. Releasing twice is nothing; releasing never is a key
        // held for a job that no longer exists.
        keyring.Release(fetchId);

        return true;
    }

    public async Task<int> DismissFinishedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // The ids first, so every key can be released — ExecuteDelete does not load rows and this
        // pass must not leave the keyring holding secrets for jobs it has just erased.
        var going = await db.RemoteFetches
            .Where(f => f.TenantId == tenantId
                && f.Status != RemoteFetchStatus.Queued
                && f.Status != RemoteFetchStatus.Running)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        if (going.Count == 0) return 0;

        var removed = await db.RemoteFetches
            .Where(f => going.Contains(f.Id))
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var id in going) keyring.Release(id);

        return removed;
    }
}
