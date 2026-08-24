using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

public class TelegramRateLimiterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_first_message_to_a_chat_waits_for_nothing()
    {
        var limiter = new TelegramRateLimiter(new FixedClock(Now));

        (await limiter.ReserveAsync(1, CancellationToken.None)).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_second_message_to_the_same_chat_waits_a_second()
    {
        var limiter = new TelegramRateLimiter(new FixedClock(Now));

        await limiter.ReserveAsync(1, CancellationToken.None);
        var second = await limiter.ReserveAsync(1, CancellationToken.None);

        // Telegram's own guidance, verbatim: avoid sending more than one message per second to the
        // same chat, and eventually you begin receiving 429s.
        second.Should().Be(TelegramRateLimiter.PerChatInterval);
    }

    [Fact]
    public async Task Two_chats_do_not_wait_for_each_other_beyond_the_global_spacing()
    {
        var limiter = new TelegramRateLimiter(new FixedClock(Now));

        await limiter.ReserveAsync(1, CancellationToken.None);
        var other = await limiter.ReserveAsync(2, CancellationToken.None);

        // One customer's chat must not make the bot look broken for everybody else. The only thing
        // the second chat pays is the global spacing, which is 25 a second rather than the stated
        // ~30 — headroom, so we never learn where the real ceiling is from a 429.
        other.Should().Be(TelegramRateLimiter.GlobalInterval);
    }

    [Fact]
    public async Task The_global_bucket_paces_a_burst_across_many_chats()
    {
        var limiter = new TelegramRateLimiter(new FixedClock(Now));

        TimeSpan last = TimeSpan.Zero;
        for (var chat = 1; chat <= 25; chat++)
        {
            last = await limiter.ReserveAsync(chat, CancellationToken.None);
        }

        // Twenty-five calls, each 40 ms after the last: the twenty-fifth lands at 960 ms, so the
        // rate is 25/s and never 30.
        last.Should().Be(TelegramRateLimiter.GlobalInterval * 24);
    }

    [Fact]
    public async Task A_reservation_is_taken_at_the_moment_it_is_computed()
    {
        var limiter = new TelegramRateLimiter(new FixedClock(Now));

        var first = limiter.ReserveAsync(7, CancellationToken.None);
        var second = limiter.ReserveAsync(7, CancellationToken.None);

        var waits = await Task.WhenAll(first, second);

        // Two callers racing for one chat must not both be told "no wait" and then both send. The
        // reservation and the arithmetic are one step behind one gate, so the two answers are
        // distinct by construction.
        waits.Should().OnlyHaveUniqueItems();
    }
}
