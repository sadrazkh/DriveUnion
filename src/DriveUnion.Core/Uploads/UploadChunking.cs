namespace DriveUnion.Core.Uploads;

/// <summary>
/// The arithmetic Drive imposes on resumable uploads.
///
/// Google requires every chunk except the last to be a multiple of 256 KiB. Violating it does not
/// fail loudly at the offending chunk — the session simply stops acknowledging bytes, which reads
/// like a stalled network. Checking it here makes it a unit test instead of a support ticket.
/// </summary>
public static class UploadChunking
{
    public const int DriveChunkMultiple = 256 * 1024;

    /// <summary>32 MiB — 128 × the required multiple. Large enough that per-chunk overhead is noise,
    /// small enough that a dropped chunk is a cheap retry.</summary>
    public const int DefaultChunkSize = 32 * 1024 * 1024;

    public const int MinChunkSize = DriveChunkMultiple;
    public const int MaxChunkSize = 256 * 1024 * 1024;

    public static bool IsValidChunkSize(int chunkSize) =>
        chunkSize >= MinChunkSize
        && chunkSize <= MaxChunkSize
        && chunkSize % DriveChunkMultiple == 0;

    /// <summary>
    /// A chunk is acceptable when it lands at the offset Google is waiting for and is either the
    /// final chunk or a clean multiple of 256 KiB.
    /// </summary>
    public static bool IsValidChunk(long offset, long length, long totalSize)
    {
        if (offset < 0 || length <= 0 || totalSize < 0) return false;
        if (offset + length > totalSize) return false;

        var isFinal = offset + length == totalSize;
        return isFinal || length % DriveChunkMultiple == 0;
    }

    /// <summary>The value of the <c>Content-Range</c> header for a chunk: <c>bytes 0-1023/4096</c>.</summary>
    public static string ContentRange(long offset, long length, long totalSize) =>
        $"bytes {offset}-{offset + length - 1}/{totalSize}";

    /// <summary>
    /// The <c>Content-Range</c> used to ask Google how much it has, rather than to send anything.
    /// An empty PUT carrying this is answered with 308 and a <c>Range</c> header.
    /// </summary>
    public static string ProbeContentRange(long totalSize) => $"bytes */{totalSize}";
}
