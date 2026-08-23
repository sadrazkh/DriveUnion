using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Infrastructure.Google;

namespace DriveUnion.Tests.Google;

/// <summary>
/// What the request looked like by the time it reached the wire, captured before the response is
/// produced. The body is snapshotted here because a chunk write hands over a stream that can only be
/// read once — which is the property half of these tests exist to pin down.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> ContentHeaders,
    byte[] Body,
    bool MarkedNonRewindable)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? ContentHeader(string name) =>
        ContentHeaders.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A hand-written stand-in for Google.
///
/// Hand-written on purpose: no mocking library is referenced by this solution, and the thing being
/// asserted here is the exact bytes of a header, which a matcher expression obscures rather than
/// clarifies.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private readonly List<RecordedRequest> _requests = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>
    /// Answers each call with the next response, then repeats the last one.
    ///
    /// Factories rather than instances: the retry handler disposes a response before waiting on it,
    /// so handing the same object back twice would fail the test for a reason that has nothing to do
    /// with what it is asserting.
    /// </summary>
    public static StubHttpMessageHandler Sequence(params Func<HttpResponseMessage>[] responses) =>
        new((_, attempt) => responses[Math.Min(attempt, responses.Length) - 1]());

    public static StubHttpMessageHandler Always(Func<HttpResponseMessage> response) =>
        new((_, _) => response());

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public int CallCount => _requests.Count;

    public RecordedRequest LastRequest => _requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            Flatten(request.Headers),
            request.Content is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : Flatten(request.Content.Headers),
            body,
            request.Options.TryGetValue(DriveRetryHandler.NonRewindableBody, out var flag) && flag));

        var response = _responder(request, _requests.Count);
        response.RequestMessage = request;
        return response;
    }

    private static Dictionary<string, string> Flatten(HttpHeaders headers)
    {
        var flattened = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            flattened[name] = string.Join(", ", values);
        }

        return flattened;
    }
}

internal static class StubResponses
{
    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Drive's "resume incomplete". 308 with a <c>Range</c> header naming the contiguous prefix it
    /// has actually stored — or with no header at all when it has stored nothing.
    /// </summary>
    public static HttpResponseMessage ResumeIncomplete(string? range)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308)
        {
            Content = new ByteArrayContent([]),
        };

        if (range is not null)
        {
            response.Headers.TryAddWithoutValidation("Range", range);
        }

        return response;
    }

    public static HttpResponseMessage RateLimited(string? retryAfter = null, string? reason = null)
    {
        var json = reason is null
            ? """{"error":{"code":429,"message":"Rate Limit Exceeded"}}"""
            : $$$"""
                {"error":{"code":403,"message":"Rate Limit Exceeded","errors":[{"domain":"usageLimits","reason":"{{{reason}}}"}]}}
                """;

        var status = reason is null ? HttpStatusCode.TooManyRequests : HttpStatusCode.Forbidden;
        var response = Json(status, json);

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }
}
