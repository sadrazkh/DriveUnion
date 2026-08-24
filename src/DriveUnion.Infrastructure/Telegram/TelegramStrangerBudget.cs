using System.Collections.Concurrent;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// How many times the bot will answer somebody it has never heard of.
///
/// <para>A public bot receives messages from anyone, and the entire Telegram user-id space is
/// enumerable at whatever rate we allow. Past the budget the update is still <em>consumed</em> — the
/// offset advances, the webhook returns 200 — and <b>nothing is sent</b>. Silence rather than an
/// error, because an error is a reply and a reply is the resource being abused.</para>
///
/// <para>In memory rather than in a table, and that is a deliberate trade. A restart hands every
/// stranger a fresh budget, which costs at most three more messages each; a table would cost a write
/// on every message from every stranger, which is the load being defended against. The global cap is
/// what bounds the total either way.</para>
/// </summary>
public sealed class TelegramStrangerBudget(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>
    /// Across everybody. A thousand strangers each inside their own three-per-hour budget is still
    /// three thousand messages, so the per-sender rule alone is not a cap on anything.
    /// </summary>
    private const int GlobalPerMinute = 30;

    private readonly ConcurrentDictionary<long, (DateTimeOffset WindowStart, int Count)> _senders = new();
    private readonly Lock _globalGate = new();

    private DateTimeOffset _globalWindowStart;
    private int _globalCount;

    public bool TryTakeReply(long telegramUserId, int perSenderPerHour)
    {
        var now = clock.GetUtcNow();

        lock (_globalGate)
        {
            if (now - _globalWindowStart >= TimeSpan.FromMinutes(1))
            {
                _globalWindowStart = now;
                _globalCount = 0;
            }

            if (_globalCount >= GlobalPerMinute) return false;
        }

        var granted = false;

        _senders.AddOrUpdate(
            telegramUserId,
            _ =>
            {
                granted = perSenderPerHour > 0;
                return (now, 1);
            },
            (_, existing) =>
            {
                if (now - existing.WindowStart >= Window)
                {
                    granted = perSenderPerHour > 0;
                    return (now, 1);
                }

                if (existing.Count >= perSenderPerHour) return existing;

                granted = true;
                return (existing.WindowStart, existing.Count + 1);
            });

        if (!granted) return false;

        lock (_globalGate) _globalCount++;

        Forget(now);

        return true;
    }

    private void Forget(DateTimeOffset now)
    {
        if (_senders.Count < 4096) return;

        foreach (var entry in _senders)
        {
            if (now - entry.Value.WindowStart >= Window) _senders.TryRemove(entry.Key, out _);
        }
    }
}
