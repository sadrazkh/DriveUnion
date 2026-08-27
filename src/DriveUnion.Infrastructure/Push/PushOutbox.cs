using System.Threading.Channels;
using DriveUnion.Core.Application;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// Where «this finished» is put down, so that the thread which finished it can carry on.
///
/// <para><b>Why there is a queue at all.</b> <c>RemoteFetcher</c> and <c>DeletionRunner</c> are
/// workers with a per-pass budget, and a notification is one HTTPS request per device to a service
/// on somebody else's network. A deletion that awaited three of those before moving the next file
/// would be a deletion whose speed is decided by Apple's uptime. Raising is a
/// <see cref="ChannelWriter{T}.TryWrite"/> and returns immediately; spending the sockets is
/// <c>PushWorker</c>'s problem, on its own thread, in its own scope.</para>
///
/// <para><b>Why it is memory and not a table.</b> A row would make this survive a restart, and
/// surviving a restart is not wanted: what is in here is «a thing that happened in the last few
/// seconds», and a deployment that comes back up after ten minutes should not deliver ten minutes of
/// stale notifications to a phone whose owner has already opened the panel and seen the answer. The
/// record of what happened is the <c>RemoteFetch</c> row, the <c>DeletionJob</c> row and the abuse
/// queue — all of which are still there. This is the doorbell, not the ledger.</para>
///
/// <para><b>What being full means.</b> The oldest entry is dropped rather than the newest refused.
/// A queue this size is only ever full because delivery has stalled, and in that state the freshest
/// news is the only news worth having — a customer does not want the notification for the fetch
/// before last.</para>
/// </summary>
public sealed class PushOutbox : IPushEvents
{
    /// <summary>
    /// How many events may be waiting.
    ///
    /// <para>256. Delivery is a handful of requests, so the steady state is zero or one; this is
    /// only reached when a push service has stopped answering, and at that point the number decides
    /// how much memory a stall costs rather than how much work is kept.</para>
    /// </summary>
    public const int Capacity = 256;

    private readonly Channel<PushEvent> _events = Channel.CreateBounded<PushEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,

            // One PushWorker reads this. Stated rather than left at the default because it is a
            // fact about the design — a second reader would deliver a customer's notification twice
            // to half their devices — and because saying so is what makes the channel take the
            // cheaper path.
            SingleReader = true,
        });

    /// <summary>
    /// Never throws and never blocks.
    ///
    /// <para>The callers are inside a worker that has just finished somebody's work, or inside a
    /// request that has just accepted somebody's report. Neither of them has anything useful to do
    /// with a failure to enqueue a courtesy, and taking either of them down over one would be the
    /// tail wagging the dog. <c>TryWrite</c>'s answer is deliberately discarded: with
    /// <see cref="BoundedChannelFullMode.DropOldest"/> it is false only after the channel has been
    /// completed, which happens nowhere.</para>
    /// </summary>
    public void Raise(PushEvent notification) => _events.Writer.TryWrite(notification);

    /// <summary>
    /// Everything raised, as it is raised, until the host stops.
    ///
    /// <para>The worker awaits this rather than polling a timer: a notification whose whole value is
    /// that it arrives while the customer is still wondering is not one to hold for ten seconds.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<PushEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// One event if there is one, for a test that wants to see what the domain raised without
    /// standing a worker up around it.
    /// </summary>
    public bool TryRead(out PushEvent notification) => _events.Reader.TryRead(out notification!);

    /// <summary>How many are waiting. For a test, and for a log line that has to explain a stall.</summary>
    public int Waiting => _events.Reader.Count;
}
