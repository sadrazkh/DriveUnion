using System.Net;
using System.Text;
using DriveUnion.Infrastructure.Google;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The backoff, and the one request it must never touch.
/// </summary>
public class DriveRetryHandlerTests
{
    [Fact]
    public async Task A_429_is_retried_until_it_succeeds()
    {
        var stub = StubHttpMessageHandler.Sequence(
            () => StubResponses.RateLimited(),
            () => StubResponses.RateLimited(),
            () => StubResponses.Json(HttpStatusCode.OK, """{"id":"file-1"}"""));

        var (client, time) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.CallCount.Should().Be(3);
        time.Delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task Full_jitter_keeps_the_wait_inside_the_computed_ceiling()
    {
        var stub = StubHttpMessageHandler.Sequence(
            () => StubResponses.RateLimited(),
            () => StubResponses.Json(HttpStatusCode.OK, "{}"));

        var (client, time) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The first ceiling is the 500 ms base delay, and full jitter draws uniformly below it —
        // which is the point: twenty throttled chunks must not all wake up together.
        time.Delays.Should().ContainSingle();
        time.Delays[0].Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        time.Delays[0].Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Retry_after_beats_our_own_arithmetic()
    {
        var stub = StubHttpMessageHandler.Sequence(
            () => StubResponses.RateLimited(retryAfter: "7"),
            () => StubResponses.Json(HttpStatusCode.OK, "{}"));

        var (client, time) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        time.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_request_whose_body_cannot_be_rewound_is_sent_exactly_once()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.RateLimited());
        var (client, time) = Build(stub);

        using var request = new HttpRequestMessage(HttpMethod.Put, "https://example.invalid/session")
        {
            Content = new StreamContent(
                new MemoryStream(Encoding.UTF8.GetBytes("thirty-two mebibytes, pretend"))),
        };
        request.Options.Set(DriveRetryHandler.NonRewindableBody, true);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // The replay would send an empty body under a Content-Range still claiming 32 MiB. Google
        // accepts it, acknowledges nothing, and its confirmed prefix stops advancing — which on the
        // wire is indistinguishable from a stalled network.
        stub.CallCount.Should().Be(1);
        stub.LastRequest.MarkedNonRewindable.Should().BeTrue();
        time.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task A_403_that_is_a_rate_limit_is_retried()
    {
        var stub = StubHttpMessageHandler.Sequence(
            () => StubResponses.RateLimited(reason: "userRateLimitExceeded"),
            () => StubResponses.Json(HttpStatusCode.OK, "{}"));

        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_403_that_is_a_permission_problem_is_not()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.RateLimited(reason: "insufficientFilePermissions"));

        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task The_error_body_survives_being_inspected_for_a_reason()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.RateLimited(reason: "insufficientFilePermissions"));

        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        // The handler reads the body to classify the 403; the caller still has to be able to read it
        // afterwards to say anything useful about the failure.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("insufficientFilePermissions");
    }

    [Fact]
    public async Task A_503_is_retried()
    {
        var stub = StubHttpMessageHandler.Sequence(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(string.Empty),
            },
            () => StubResponses.Json(HttpStatusCode.OK, "{}"));

        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_501_is_not_retried_because_it_will_not_become_implemented()
    {
        var stub = StubHttpMessageHandler.Always(
            () => new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent(string.Empty),
            });

        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task The_attempt_budget_is_finite()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.RateLimited());
        var (client, _) = Build(stub);

        using var response = await client.GetAsync(new Uri("https://example.invalid/x"));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        stub.CallCount.Should().Be(5);
    }

    private static (HttpClient Client, ImmediateTimeProvider Time) Build(StubHttpMessageHandler stub)
    {
        var time = new ImmediateTimeProvider();
        var handler = new DriveRetryHandler(NullLogger<DriveRetryHandler>.Instance, time)
        {
            InnerHandler = stub,
        };

        return (new HttpClient(handler), time);
    }
}
