namespace DriveUnion.Core.Uploads;

/// <summary>
/// A read-only stream that keeps a running total of what has passed through it, and does nothing
/// else at all.
///
/// <para>It exists for the one egress path that does not copy: a Telegram delivery forwards the
/// storage stream straight into a multipart upload, unread by anything in this product, because
/// buffering a two-gigabyte delivery anywhere is a two-gigabyte bug. That left it as the one route
/// putting a customer's bytes on the wire with nothing counting them — so the operator paid Google
/// for every file the bot sent and no screen in the product could say so.</para>
///
/// <para>Nothing here buffers, copies or seeks. Each <c>Read</c> is the inner stream's own read plus
/// an addition, which is what makes this compatible with the constraint that path is written under
/// rather than an exception to it.</para>
/// </summary>
/// <param name="inner">The stream being read. This type does not own it and does not dispose it.</param>
public sealed class CountingStream(Stream inner) : Stream
{
    private long _read;

    /// <summary>
    /// How many bytes have been read so far.
    ///
    /// <para>Read after the transfer — including after a failed one, which is the case this type
    /// exists for. A delivery that died at 90% of two gigabytes cost the operator 1.8 GB, and an
    /// upload that is retried costs them both attempts; a meter that only counted the successes
    /// would under-report exactly the large files where the difference is worth money.</para>
    /// </summary>
    public long BytesRead => Interlocked.Read(ref _read);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    /// <summary>
    /// Forwarded rather than refused: an HTTP client asks a content stream for its length to decide
    /// whether it can set <c>Content-Length</c>, and a throw here would change how the request is
    /// framed. <see cref="CanSeek"/> is still false, so nothing may act on it as a position.
    /// </summary>
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException("A counting stream does not seek.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);

        if (read > 0) Interlocked.Add(ref _read, read);

        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);

        if (read > 0) Interlocked.Add(ref _read, read);

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read > 0) Interlocked.Add(ref _read, read);

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        // Nothing is held, so there is nothing to flush. Not a throw: a writer-shaped call on a
        // read-only stream is something plenty of library code makes unconditionally.
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("A counting stream does not seek.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("A counting stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A counting stream is read-only.");

    /// <summary>
    /// Does not dispose the inner stream, deliberately.
    ///
    /// <para>The caller opened it — a <c>DriveDownload</c> with its own lifetime — and is the one
    /// that closes it. A wrapper that took ownership would close the download the moment an HTTP
    /// client finished with the content stream, which is earlier than the caller's own
    /// <c>await using</c> expects and is a bug that only appears under a retry.</para>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        // Deliberately empty; see above. base.Dispose does nothing on Stream.
    }
}
