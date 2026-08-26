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
    FetchKeyring keyring,
    TimeProvider clock) : IRemoteFetches
{
    public async Task<RemoteFetchStartResult> StartAsync(
        Guid tenantId,
        Guid? ownerUserId,
        string url,
        string? secret,
        CancellationToken cancellationToken)
    {
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

        byte[]? contentKey = null;

        if (secret is { Length: > 0 })
        {
            // Sealed here, in the request the customer typed their secret into, and not in the
            // worker — so the secret does not have to outlive this method and is not written down
            // for a job that starts minutes later. What is written down is the wrapped key; what is
            // held in memory is the raw one. See FetchKeyring for what that costs on a restart.
            var salt = RandomNumberGenerator.GetBytes(Du1.SaltBytes);
            var wrapping = Du1.DeriveWrappingKey(secret, salt, Du1.KdfIterations);

            contentKey = RandomNumberGenerator.GetBytes(Du1.KeyBytes);

            fetch.KdfSalt = Convert.ToBase64String(salt);
            fetch.KdfIterations = Du1.KdfIterations;
            fetch.WrappedKey = Convert.ToBase64String(Du1.WrapKey(contentKey, wrapping));
            fetch.NoncePrefix = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(Du1.NoncePrefixBytes));

            Array.Clear(wrapping);
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
}
