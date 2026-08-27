using DriveUnion.Core.Application;
using DriveUnion.Core.Notifications;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// The device rows, and the one place a dead one is removed.
///
/// <para><b>Pruning is not a follow-up and it is not a sweep somebody remembers to write.</b> An
/// endpoint that has stopped existing costs a request, a socket and a timeout on every notification
/// for the life of the deployment, and nothing anywhere reports it — the notification the customer
/// was expecting simply does not arrive and the log line says «failed» for the hundredth time. So
/// there are three ways a row leaves this table and all three are here: the push service says the
/// endpoint is gone, the sends keep failing, or nothing has heard from the device in three months.
/// </para>
///
/// <para>Every read carries whose devices it wants. This model has no global query filter and must
/// not have one — <c>/d/{slug}</c> is anonymous and a filter would resolve to <c>Guid.Empty</c> —
/// so tenant scoping is an argument here as it is everywhere else in this codebase.</para>
/// </summary>
public sealed class PushSubscriptionStore(DriveUnionDbContext db, TimeProvider clock) : IPushSubscriptions
{
    public async Task<PushSubscriptionSaved> SaveAsync(
        Guid? tenantId,
        Guid userId,
        string endpoint,
        string p256dh,
        string auth,
        string culture,
        CancellationToken cancellationToken)
    {
        if (!IsEndpoint(endpoint) || !IsPublicKey(p256dh) || !IsAuthSecret(auth))
        {
            return new PushSubscriptionSaved(null, PushSubscriptionRefusal.Malformed);
        }

        var now = clock.GetUtcNow();

        // By endpoint and not by (user, endpoint): the endpoint is the device's identity, and a
        // shared computer whose previous user is still on a row would otherwise keep receiving the
        // previous user's notifications on a browser somebody else is now signed into.
        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);

        if (existing is not null)
        {
            existing.TenantId = tenantId;
            existing.UserId = userId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.Culture = culture;
            existing.LastSeenAt = now;

            // Re-subscribing is the device saying it is alive, which is exactly what the counter is
            // counting the absence of. Leaving it would mean a device that came back after a bad
            // week was still one failure from being forgotten.
            existing.ConsecutiveFailures = 0;
            existing.LastFailureReason = null;

            await db.SaveChangesAsync(cancellationToken);

            return new PushSubscriptionSaved(existing.Id, PushSubscriptionRefusal.None);
        }

        var held = await db.PushSubscriptions.CountAsync(s => s.UserId == userId, cancellationToken);

        if (held >= PushSubscription.MostPerUser)
        {
            return new PushSubscriptionSaved(null, PushSubscriptionRefusal.TooMany);
        }

        var subscription = new PushSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            Culture = culture,
            CreatedAt = now,
            LastSeenAt = now,
        };

        db.PushSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);

        return new PushSubscriptionSaved(subscription.Id, PushSubscriptionRefusal.None);
    }

    public async Task<bool> RemoveAsync(Guid userId, string endpoint, CancellationToken cancellationToken)
    {
        // The user id is on the predicate rather than checked after the read, the same shape every
        // tenant-scoped statement in this codebase takes: somebody else's device is never matched
        // rather than found and then refused.
        var removed = await db.PushSubscriptions
            .Where(s => s.UserId == userId && s.Endpoint == endpoint)
            .ExecuteDeleteAsync(cancellationToken);

        return removed > 0;
    }

    public async Task<IReadOnlyList<PushSubscription>> ForAsync(
        PushAudience audience,
        CancellationToken cancellationToken)
    {
        if (audience.Operators)
        {
            // Asked of the user rows rather than of a flag on the subscription. Whether somebody is
            // operator staff is a fact that changes — it is on AppUser, it is what the panel's own
            // policy authorises on — and a copy of it here would keep notifying a person about abuse
            // reports for as long as nobody thought to go and update their devices.
            return await db.PushSubscriptions
                .Where(s => db.Users.Any(u => u.Id == s.UserId && u.IsOperator))
                .ToListAsync(cancellationToken);
        }

        if (audience.TenantId is not { } tenantId) return [];

        var scoped = db.PushSubscriptions.Where(s => s.TenantId == tenantId);

        if (audience.UserId is { } userId) scoped = scoped.Where(s => s.UserId == userId);

        return await scoped.ToListAsync(cancellationToken);
    }

    public async Task<int> CountForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.PushSubscriptions.CountAsync(s => s.UserId == userId, cancellationToken);

    public async Task RecordAsync(
        Guid subscriptionId,
        PushDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.Outcome == PushDeliveryOutcome.Gone)
        {
            // A 404 or a 410 is the one unambiguous answer in this protocol. Nothing is counted and
            // nothing is retried: the browser profile is gone.
            await db.PushSubscriptions
                .Where(s => s.Id == subscriptionId)
                .ExecuteDeleteAsync(cancellationToken);

            return;
        }

        var subscription = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

        if (subscription is null) return;

        if (delivery.Outcome == PushDeliveryOutcome.Accepted)
        {
            subscription.ConsecutiveFailures = 0;
            subscription.LastFailureReason = null;
            subscription.LastSeenAt = clock.GetUtcNow();

            await db.SaveChangesAsync(cancellationToken);

            return;
        }

        subscription.ConsecutiveFailures++;
        subscription.LastFailureReason = Trimmed(delivery.Reason);

        if (subscription.ConsecutiveFailures >= PushSubscription.MaxConsecutiveFailures)
        {
            // Five separate failures is not a bad afternoon. Removing the row is what stops the
            // queue carrying it for ever; the customer's remedy is one tap on the notifications
            // screen, which is a great deal better than notifications that quietly stop for
            // everybody because one dead endpoint is eating the budget.
            db.PushSubscriptions.Remove(subscription);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> SweepStaleAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - PushSubscription.StaleAfter;

        // Compared in memory, and the projection is why that is affordable: SQLite will not compare
        // a DateTimeOffset in SQL, the tests run on SQLite and production is Postgres, so a WHERE
        // over this column would behave differently in the two places. Two columns per row is what
        // it costs to have it behave the same.
        var stale = await db.PushSubscriptions
            .AsNoTracking()
            .Select(s => new { s.Id, s.LastSeenAt })
            .ToListAsync(cancellationToken);

        var doomed = stale.Where(s => s.LastSeenAt < cutoff).Select(s => s.Id).ToList();

        if (doomed.Count == 0) return 0;

        return await db.PushSubscriptions
            .Where(s => doomed.Contains(s.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// An absolute https URL inside the column.
    ///
    /// <para>https and not «any scheme»: the endpoint is somewhere this server will POST an
    /// encrypted body to, chosen by a value that arrived from a browser. Everything else about that
    /// request is guarded by the push service being a push service; the scheme is the one part a
    /// caller could otherwise use to point this server somewhere of their own — which is the whole
    /// of what <c>RemoteAddressPolicy</c> exists for on the one other path where a customer names an
    /// address.</para>
    /// </summary>
    private static bool IsEndpoint(string endpoint) =>
        endpoint.Length <= PushSubscription.MaxEndpointLength
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsPublicKey(string p256dh) =>
        p256dh.Length <= PushSubscription.MaxP256dhLength
        && Base64UrlText.Decode(p256dh) is { Length: WebPushEncryption.PublicKeyLength } bytes
        && bytes[0] == 0x04;

    private static bool IsAuthSecret(string auth) =>
        auth.Length <= PushSubscription.MaxAuthLength
        && Base64UrlText.Decode(auth) is { Length: WebPushEncryption.AuthSecretLength };

    private static string? Trimmed(string? reason) =>
        reason is null || reason.Length <= PushSubscription.MaxFailureReasonLength
            ? reason
            : reason[..PushSubscription.MaxFailureReasonLength];
}
