using System.Net;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// A chunk body, forwarded from the caller's stream to Google without being buffered and without
/// being closed.
///
/// <see cref="StreamContent"/> would do the forwarding, but it takes ownership: disposing the
/// request disposes the stream, and the stream here is the ASP.NET request body, which the pipeline
/// is still responsible for. Copying instead is not an option either — a 32 MiB chunk held per
/// concurrent upload is how a streaming server runs out of memory.
///
/// The stream can be sent exactly once. That is why every request built on this content is marked
/// <see cref="DriveRetryHandler.NonRewindableBody"/>.
/// </summary>
internal sealed class ForwardedStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _length;

    public ForwardedStreamContent(Stream source, long length)
    {
        _source = source;
        _length = length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        _source.CopyToAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        _source.CopyToAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        // Known up front, so the request goes out with a Content-Length rather than chunked
        // transfer-encoding — which Drive's resumable endpoint expects alongside Content-Range.
        length = _length;
        return true;
    }
}
