using System.Collections.Concurrent;
using System.Diagnostics;

namespace DriveUnion.Infrastructure;

/// <summary>How long ago a named background loop was last known to be turning.</summary>
/// <param name="Loop">
/// The loop's own class name. It is a diagnostic and it reaches an anonymous caller through
/// <c>/readyz</c>, so it must stay what it is — a type name — and must never grow into a message.
/// </param>
/// <param name="Since">
/// <see cref="TimeSpan.Zero"/> while a pass is in flight, and the time since the last one ended
/// otherwise.
/// </param>
public readonly record struct WorkerSilence(string Loop, TimeSpan Since);

/// <summary>
/// The one thing a background loop tells the outside world: that it is still going round.
///
/// <para><b>Why this exists.</b> <c>/readyz</c> has to be able to say that the half of this process
/// nobody can see is alive. A loop that has stopped — a faulted <c>ExecuteAsync</c>, a host that
/// never started, a thread pool with nothing left to give — produces no error and no log line; it
/// produces files that are never deleted, locks that are never sealed and a catalogue that is never
/// backed up, and the first report of it is a customer weeks later. This is the cheapest thing that
/// turns that silence into an answer.</para>
///
/// <para><b>Why it is static.</b> Every alternative was a worse trade. A DI singleton would mean a
/// constructor parameter on five workers whose registration extensions live in this project and are
/// called by test hosts that do not register it — so the failure mode of forgetting one line becomes
/// a container that will not boot, rather than a heartbeat that is missing. Writing it to the
/// database would mean a migration and a write every few seconds for a fact that is worthless the
/// moment the process ends. The state here is process-wide because the question is process-wide:
/// there is one process, and the probe is asking about it.</para>
///
/// <para><b>The hazard that comes with that.</b> A test that runs one of these loops for real writes
/// into the same dictionary as every other test in the process, and nothing resets it. No test in
/// this suite does — every in-process host calls <c>RemoveEveryBackgroundLoop</c>, and the one
/// worker a test constructs directly (<c>TrashPurgeService</c>) is deliberately not instrumented.
/// A test that starts one of the five below is the thing to look at if a readiness assertion
/// elsewhere starts flickering.</para>
///
/// <para><b>Why a disposable scope rather than a bare timestamp.</b> A pass is not short. A remote
/// fetch is a whole file down somebody else's connection; sealing a six-gigabyte film is minutes.
/// A heartbeat stamped only at the top of a pass would report a busy loop as a dead one, so
/// readiness would go red exactly when the deployment was working hardest — which is the worst
/// possible time to be taken out of rotation. A loop inside a pass is reported as
/// <see cref="TimeSpan.Zero"/>, because it is the most alive it can be.</para>
///
/// <para><b>Which loops are in and which are not.</b> In: the five whose idle interval is a minute
/// or less, so a five-minute window is five missed turns rather than a coin toss. Out, on purpose:
/// <c>TrashPurgeService</c>, whose interval is configurable and five minutes by default — covering
/// it would mean widening the window until it could no longer see the fast loops stop;
/// <c>S3StagingSweeper</c>, which runs hourly; <c>PushWorker</c>, which reads a channel and is
/// legitimately idle for days; and the Telegram loops, which only exist when a bot token is
/// configured, so a deployment without one would never become ready.</para>
///
/// <para>Timestamps are <see cref="Stopwatch"/>'s rather than a clock's. This measures an elapsed
/// interval inside one process, and a wall clock that steps — an NTP correction, a container
/// resumed from a pause — would either invent a stall or hide one.</para>
/// </summary>
public static class WorkerHeartbeat
{
    private static readonly ConcurrentDictionary<string, Pulse> Loops = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks the start of one pass of <paramref name="loop"/>. Dispose it when the pass ends — a
    /// <c>using var</c> at the top of the loop body is the whole of the call site.
    /// </summary>
    public static IDisposable Beat(string loop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loop);

        var pulse = Loops.GetOrAdd(loop, static _ => new Pulse());

        pulse.Enter();

        return pulse;
    }

    /// <summary>
    /// Every loop that has ever reported in, and how long it has been quiet.
    ///
    /// <para>Only loops that have run at least once are here, and that is deliberate rather than a
    /// gap that was overlooked. A readiness check has no list of the loops a deployment is supposed
    /// to have — the registrations are lines in <c>Program.cs</c> and a host is free to leave any of
    /// them out — so the alternative to "report what has reported" is a probe that fails on a
    /// perfectly good web-only replica for ever. The cost is the one case this cannot see: a loop
    /// that dies before its very first pass. All five of them take their first pass within seconds
    /// of the host starting, which is what makes that a small price rather than a hole.</para>
    /// </summary>
    public static IReadOnlyList<WorkerSilence> Silences() =>
        Loops.Select(entry => new WorkerSilence(entry.Key, entry.Value.Silence())).ToList();

    /// <summary>One loop's state: how many passes are in flight, and when the last one ended.</summary>
    private sealed class Pulse : IDisposable
    {
        /// <summary>
        /// Seeded at construction so there is no window in which a loop exists and has neither
        /// finished a pass nor started one — that state would read as an infinite silence.
        /// </summary>
        private long _finishedAt = Stopwatch.GetTimestamp();

        private int _inFlight;

        public void Enter() => Interlocked.Increment(ref _inFlight);

        public void Dispose()
        {
            // Written before the counter drops, so a reader can never see zero in flight beside a
            // stale finish time.
            Volatile.Write(ref _finishedAt, Stopwatch.GetTimestamp());

            Interlocked.Decrement(ref _inFlight);
        }

        public TimeSpan Silence() =>
            Volatile.Read(ref _inFlight) > 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(Volatile.Read(ref _finishedAt));
    }
}
