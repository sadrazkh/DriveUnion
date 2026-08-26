using System.Buffers;
using System.Globalization;
using System.Text;

namespace DriveUnion.Web.S3;

/// <summary>
/// Unwraps <c>aws-chunked</c> so what reaches storage is the object and not its framing.
///
/// <para><b>Why this exists at all.</b> When a client sets
/// <c>x-amz-content-sha256: STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> — which the AWS CLI and boto3 do
/// by default on any upload — the body on the wire is not the object. It is the object cut into
/// chunks, each one preceded by a line of the form <c>{hex length};chunk-signature={hex}\r\n</c> and
/// followed by <c>\r\n</c>, terminated by a zero-length chunk. A gateway that streams that body
/// straight through stores a file with several hundred bytes of protocol sprinkled through it, and
/// <b>nothing fails</b>: the upload succeeds, the size is wrong by a little, and the file is corrupt
/// in a way nobody notices until they open it.</para>
///
/// <para><b>What is and is not verified.</b> The seed signature on the request is checked in full,
/// so the request is authenticated. The per-chunk signatures are parsed and skipped rather than
/// recomputed. That is a real limit and here is the reasoning: a chunk signature protects a body
/// against tampering in transit, and this gateway is only reachable over TLS, which already does.
/// What it would additionally buy is detection of a compromised TLS terminator — a threat that
/// would also hold the session and could simply mint its own requests. Stated here rather than
/// implied, because «verified the signature» reads as more than it is.</para>
///
/// <para>Forward-only and never buffered whole: a 96 GB upload that gets spooled anywhere is a
/// 96 GB bug, which is the same rule the panel's own chunk path follows.</para>
/// </summary>
public sealed class AwsChunkedStream(Stream inner) : Stream
{
    private long _remainingInChunk;
    private bool _finished;

    /// <summary>
    /// The longest chunk header this will read before giving up.
    ///
    /// <para>A header is a hex length, a semicolon, <c>chunk-signature=</c> and 64 hex characters —
    /// under a hundred bytes. The cap is what stops a body that never sends a newline from being
    /// read into memory for ever: without it, «\r\n eventually» is an assumption about a stranger's
    /// bytes.</para>
    /// </summary>
    private const int MaxHeaderLength = 512;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_finished || buffer.Length == 0) return 0;

        if (_remainingInChunk == 0)
        {
            var header = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);

            if (header is null)
            {
                _finished = true;
                return 0;
            }

            _remainingInChunk = header.Value;

            // The zero-length chunk is the terminator. What follows it is a trailer nobody here
            // reads — the connection ends either way.
            if (_remainingInChunk == 0)
            {
                _finished = true;
                return 0;
            }
        }

        var wanted = (int)Math.Min(buffer.Length, _remainingInChunk);
        var read = await inner.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);

        if (read == 0)
        {
            // The body ended inside a chunk. Truncating silently would store a short file and call
            // it a success, which is the failure this whole class exists to prevent.
            throw new EndOfStreamException("The aws-chunked body ended inside a chunk.");
        }

        _remainingInChunk -= read;

        // The CRLF that closes a chunk is consumed here rather than on the next read, so a caller
        // that stops early never leaves the stream pointing at two stray bytes.
        if (_remainingInChunk == 0) await ExpectCrlfAsync(cancellationToken).ConfigureAwait(false);

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// The declared length of the next chunk, or null at the end of the body.
    ///
    /// <para>Read a byte at a time on purpose. Anything larger would over-read into the chunk's own
    /// payload, and this stream has nowhere to put bytes it was not asked for — the whole point is
    /// that it never buffers.</para>
    /// </summary>
    private async Task<long?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder(64);
        var one = ArrayPool<byte>.Shared.Rent(1);

        try
        {
            var previous = '\0';

            while (line.Length <= MaxHeaderLength)
            {
                var read = await inner.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

                if (read == 0) return line.Length == 0 ? null : throw new EndOfStreamException(
                    "The aws-chunked body ended inside a chunk header.");

                var c = (char)one[0];

                if (previous == '\r' && c == '\n')
                {
                    // The header without its trailing CR.
                    var header = line.ToString(0, line.Length - 1);
                    var semicolon = header.IndexOf(';', StringComparison.Ordinal);
                    var hex = semicolon < 0 ? header : header[..semicolon];

                    return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length)
                        && length >= 0
                        ? length
                        : throw new InvalidDataException($"«{hex}» is not a chunk length.");
                }

                line.Append(c);
                previous = c;
            }

            throw new InvalidDataException("An aws-chunked header ran past what a header can be.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(one);
        }
    }

    private async Task ExpectCrlfAsync(CancellationToken cancellationToken)
    {
        var pair = ArrayPool<byte>.Shared.Rent(2);

        try
        {
            var read = 0;

            while (read < 2)
            {
                var got = await inner.ReadAsync(pair.AsMemory(read, 2 - read), cancellationToken).ConfigureAwait(false);

                if (got == 0) throw new EndOfStreamException("An aws-chunked chunk was not closed.");

                read += got;
            }

            if (pair[0] != (byte)'\r' || pair[1] != (byte)'\n')
            {
                throw new InvalidDataException("An aws-chunked chunk was not followed by CRLF.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pair);
        }
    }
}
