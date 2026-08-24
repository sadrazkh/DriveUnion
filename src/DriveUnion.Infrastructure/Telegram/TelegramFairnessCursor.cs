using System.Collections.Concurrent;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// When each tenant was last served, so the outbox drains round-robin rather than first-in-first-out.
///
/// <para><b>This is a deliberate divergence from the job queue's own decision.</b> That queue declines
/// per-tenant fairness on the grounds that it is invisible at two tenants and obvious at twenty. Here
/// it is not optional, because the shared resource is a <em>single bot identity</em> with one global
/// ceiling: one tenant's backlog is directly every other tenant's latency, from the second tenant
/// onwards. And at a two-gigabyte ceiling one tenant queueing fifty deliveries occupies both transfer
/// slots for hours, so the ordering is applied to the transfer slots specifically and not only to the
/// queue as a whole.</para>
///
/// <para>In memory rather than in a column, and that is the right trade rather than a shortcut.
/// Fairness is a scheduling property of a running drainer, not a durable fact: after a restart every
/// tenant starts equal, which is exactly the state fairness is trying to produce. A column would cost
/// a write per item to remember something whose whole purpose expires with the process.</para>
/// </summary>
public sealed class TelegramFairnessCursor
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastServed = new();

    /// <summary>Never served in this process is the front of the queue, which is what makes a new
    /// tenant's single item overtake an established tenant's hundred.</summary>
    public DateTimeOffset LastServed(Guid tenantId) =>
        _lastServed.TryGetValue(tenantId, out var when) ? when : DateTimeOffset.MinValue;

    public void Served(Guid tenantId, DateTimeOffset when) => _lastServed[tenantId] = when;
}
