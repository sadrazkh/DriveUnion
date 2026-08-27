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

    /// <summary>
    /// «I do not yet know how long this file is», which Drive spells <c>*</c> in a
    /// <c>Content-Range</c>.
    ///
    /// <para><b>Not for uploading a customer's file, ever.</b> Every one of those arrives with a
    /// length — a browser has the <c>File</c>, the API declares it, and <c>RemoteFetcher</c> refuses
    /// a source that will not say — and the length is what lets the plan's ceiling and the
    /// workspace's quota be enforced <i>before</i> a byte is read. An upload whose size is unknown
    /// is an upload nothing can refuse.</para>
    ///
    /// <para>It exists for the one writer that genuinely cannot know: the catalogue backup, which
    /// gzips a hundred thousand rows straight into the pool. Its length is whatever the compressor
    /// produces, and the alternatives are to hold the whole snapshot in memory or to generate it
    /// twice and pray the two passes agree. Google's resumable protocol has a mode for exactly this
    /// — every chunk but the last carries <c>/*</c>, and the last one carries the real total, which
    /// by then is known.</para>
    /// </summary>
    public const long UnknownTotal = -1;

    public static bool IsValidChunkSize(int chunkSize) =>
        chunkSize >= MinChunkSize
        && chunkSize <= MaxChunkSize
        && chunkSize % DriveChunkMultiple == 0;

    /// <summary>
    /// A chunk is acceptable when it lands at the offset Google is waiting for and is either the
    /// final chunk or a clean multiple of 256 KiB.
    ///
    /// <para>A total of <see cref="UnknownTotal"/> is by definition not the final chunk — the writer
    /// says how long the file is by naming the total on the chunk that ends it — so the multiple is
    /// required and nothing is compared against an end that has not been decided.</para>
    /// </summary>
    public static bool IsValidChunk(long offset, long length, long totalSize)
    {
        if (offset < 0 || length <= 0) return false;

        if (totalSize == UnknownTotal) return length % DriveChunkMultiple == 0;

        if (totalSize < 0) return false;
        if (offset + length > totalSize) return false;

        var isFinal = offset + length == totalSize;
        return isFinal || length % DriveChunkMultiple == 0;
    }

    /// <summary>The value of the <c>Content-Range</c> header for a chunk: <c>bytes 0-1023/4096</c>.</summary>
    public static string ContentRange(long offset, long length, long totalSize) =>
        totalSize == UnknownTotal
            ? $"bytes {offset}-{offset + length - 1}/*"
            : $"bytes {offset}-{offset + length - 1}/{totalSize}";

    /// <summary>
    /// The <c>Content-Range</c> used to ask Google how much it has, rather than to send anything.
    /// An empty PUT carrying this is answered with 308 and a <c>Range</c> header.
    /// </summary>
    public static string ProbeContentRange(long totalSize) => $"bytes */{totalSize}";
}
