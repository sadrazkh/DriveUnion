namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// A read-only view of one byte range of an open file.
///
/// A 206 needs the response body to stop at the end of the range, and a <see cref="FileStream"/> on
/// its own cannot be told to. The alternative — reading the slice into an array and handing that back
/// — is the exact thing the download path exists to avoid: a 214 GB file must cost this server a
/// buffer, not a copy. So the file stays open, seeked to the start of the range, and this clamps
/// every read to what is left of it. No byte of the file is ever held.
///
/// <see cref="Source"/> is public because it is the evidence: what a caller receives is a window onto
/// a file on disk, not a copy of one, and a test should be able to say so.
/// </summary>
public sealed class FileWindowStream : Stream
{
    private readonly long _start;
    private readonly long _length;
    private bool _closed;

    public FileWindowStream(FileStream source, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Source = source;
        _start = start;
        _length = length;

        source.Position = start;
    }

    /// <summary>The file this is a window onto, still open and still on disk.</summary>
    public FileStream Source { get; }

    public override bool CanRead => !_closed;

    public override bool CanSeek => !_closed && Source.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => Math.Clamp(Source.Position - _start, 0, _length);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Source.Position = _start + value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var take = Available(buffer.Length);

        return take == 0 ? 0 : Source.Read(buffer[..take]);
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var take = Available(buffer.Length);

        return take == 0
            ? ValueTask.FromResult(0)
            : Source.ReadAsync(buffer[..take], cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        Position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("A download window is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A download window is read-only.");

    protected override void Dispose(bool disposing)
    {
        _closed = true;

        // The window owns the handle it was given: whoever disposes the window is done with the file.
        // DriveDownload disposes both this and the owner behind it, and disposing a FileStream twice
        // costs nothing.
        if (disposing) Source.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _closed = true;

        await Source.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>How much of a requested read the window still has left to give.</summary>
    private int Available(int requested)
    {
        var remaining = _length - Position;

        return remaining <= 0 ? 0 : (int)Math.Min(requested, remaining);
    }
}
