namespace DriveUnion.Tests.Google;

/// <summary>
/// A clock that never moves and timers that fire at once.
///
/// The backoff is the thing under test, not the waiting: this records the delay that was asked for
/// and then grants it immediately, so "Retry-After: 7" can be asserted as seven seconds without the
/// test suite spending seven seconds proving it.
/// </summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];

    public ImmediateTimeProvider(DateTimeOffset? now = null) =>
        Now = now ?? new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moved by hand when a test needs two events to have happened in a known order.</summary>
    public DateTimeOffset Now { get; set; }

    public IReadOnlyList<TimeSpan> Delays => _delays;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_delays)
        {
            _delays.Add(dueTime);
        }

        return new ImmediateTimer(callback, state);
    }

    /// <summary>
    /// Fires on the thread pool rather than inline: the caller is <c>Task.Delay</c>, which is still
    /// wiring up the timer it is being handed at the moment this runs.
    /// </summary>
    private sealed class ImmediateTimer : ITimer
    {
        public ImmediateTimer(TimerCallback callback, object? state) =>
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
