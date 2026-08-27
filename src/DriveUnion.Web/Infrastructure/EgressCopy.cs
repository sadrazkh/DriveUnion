using System.Buffers;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// The copy that counts, and the only one bytes leaving storage are allowed to travel by.
///
/// <para>Every route in this product that puts a stored file on the wire costs the operator exactly
/// the same: Google bills egress out of the pool account, and it does not care whether the request
/// carried a session cookie, an API key, an S3 signature or nothing at all. For a while only the
/// public link path counted, so the operator's own «what has this product served» chart was drawing
/// a subset and calling it the total, and a workspace over its monthly allowance could still pull
/// terabytes through <c>/api/v1</c> or <c>/s3</c> — the two routes best suited to pulling
/// terabytes.</para>
///
/// <para>This lived as a private method on <c>PublicDownloadController</c>. It is here now because
/// three call sites need it and a second hand-written copy loop is how one of them ends up counting
/// something slightly different from the others.</para>
/// </summary>
public static class EgressCopy
{
    /// <summary>
    /// 80 KB, which is what <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/> uses. This is
    /// that copy with one addition, not a slower one chosen to make counting possible.
    /// </summary>
    private const int BufferSize = 80 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/>, reporting the running
    /// total to <paramref name="sent"/> as it goes.
    ///
    /// <para><b>Why a callback and not a return value.</b> The transfers worth counting are the ones
    /// that do not finish — a tab closed halfway, a CLI interrupted, a player that stopped seeking —
    /// and a returned total never arrives when the copy throws. Reported after each write, the
    /// caller's own variable already holds what actually reached the reader by the time the exception
    /// unwinds, so the <c>finally</c> that records it has the true figure rather than nothing or the
    /// whole file.</para>
    /// </summary>
    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        Action<long> sent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sent);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var total = 0L;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                total += read;
                sent(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
