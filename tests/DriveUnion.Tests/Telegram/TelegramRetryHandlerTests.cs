using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DriveUnion.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The short retry that keeps a transient 502 from becoming a visible failure — and, more
/// importantly, the one request shape it must never touch.
/// </summary>
public class TelegramRetryHandlerTests
{
    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        var stub = new CountingHandler((_, attempt) => attempt < 3
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK));

        using var client = NewClient(stub);

        var response = await client.PostAsync("https://telegram.invalid/x", Body());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task A_document_upload_is_attempted_exactly_once()
    {
        var stub = new CountingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        using var client = NewClient(stub);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://telegram.invalid/x")
        {
            Content = Body(),
        };

        request.Options.Set(TelegramRetryHandler.NonRewindableBody, true);

        var response = await client.SendAsync(request);

        // A document body is a caller-owned forward-only stream that has already been drained into
        // the first attempt. A retry would send an empty or partial body under a content length
        // claiming two gigabytes, and the server would not say so — the call simply fails in a way
        // that looks like a network fault. So it is attempted once and the failure is surfaced to
        // the caller, who still has the file.
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        stub.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_400_is_not_retried_because_no_retry_can_fix_it()
    {
        var stub = new CountingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));

        using var client = NewClient(stub);

        await client.PostAsync("https://telegram.invalid/x", Body());

        stub.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_retry_after_is_honoured_rather_than_argued_with()
    {
        var immediate = new ImmediateClock();

        var stub = new CountingHandler((_, attempt) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (attempt == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

            return response;
        });

        using var client = NewClient(stub, immediate);

        await client.PostAsync("https://telegram.invalid/x", Body());

        // Capped, and deliberately low. A long flood-control park belongs to the outbox, which can
        // put the item down and pick up another tenant's work; holding a worker asleep instead is how
        // one chat's throttle becomes everybody's.
        immediate.Delays.Should().NotBeEmpty();
        immediate.Delays[0].Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    private static StringContent Body() => new("{}", Encoding.UTF8, "application/json");

    private static HttpClient NewClient(CountingHandler stub, TimeProvider? clock = null) =>
        new(new TelegramRetryHandler(
            NullLogger<TelegramRetryHandler>.Instance,
            clock ?? new ImmediateClock())
        {
            InnerHandler = stub,
        });

    private sealed class CountingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;

            return Task.FromResult(responder(request, Attempts));
        }
    }

    /// <summary>
    /// Records the delay that was asked for and grants it at once: the backoff is the thing under
    /// test, not the waiting.
    /// </summary>
    private sealed class ImmediateClock : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_delays) return [.. _delays];
            }
        }

        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_delays) _delays.Add(dueTime);

            return new ImmediateTimer(callback, state);
        }

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
}
