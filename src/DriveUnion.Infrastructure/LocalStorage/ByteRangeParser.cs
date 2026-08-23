using System.Globalization;
using DriveUnion.Core.Abstractions;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>One byte range, inclusive at both ends, as HTTP counts them.</summary>
internal readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;
}

/// <summary>
/// The <c>Range</c> header, resolved the way Drive resolves it.
///
/// <c>GoogleDriveClient</c> forwards the header untouched and lets Google decide, which is
/// correct there and impossible here — this backend <em>is</em> the server, so the semantics have to
/// be written down. The three shapes that matter are <c>bytes=a-b</c>, <c>bytes=a-</c> (a resumed
/// download) and <c>bytes=-n</c> (the tail, which is how a player finds an MP4's moov atom).
///
/// Two decisions are worth naming:
///
/// A range that parses and can be served is answered 206 even when the slice is the whole file.
/// <c>Range: bytes=0-</c> on a 4096-byte file is the commonest request a browser makes, and it comes
/// back <c>206 · bytes 0-4095/4096</c>. Deciding partialness by whether the slice happens to cover
/// everything would answer 200 there and leave the busiest branch of the download path unproven.
///
/// A header this does not understand is ignored and the whole file is served, which is what RFC 9110
/// requires and what Drive does. A header it understands but cannot satisfy is a 416, and
/// <see cref="DriveDownload"/> has no shape for one — so it throws, exactly as the real client does
/// when Google answers 416.
/// </summary>
internal static class ByteRangeParser
{
    private const string Prefix = "bytes=";

    public static ByteRange? Resolve(string? rangeHeader, long totalSize)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return null;

        var value = rangeHeader.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        // Only the first range of a multipart request. Drive serves one range and so does the public
        // download path above this; a multipart/byteranges body is not something either can produce.
        var spec = value[Prefix.Length..].Split(',')[0].Trim();

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return null;

        var head = spec[..dash].Trim();
        var tail = spec[(dash + 1)..].Trim();
        var last = totalSize - 1;

        long start;
        long end;

        if (head.Length == 0)
        {
            // "bytes=-500": the last 500 bytes, and all of them if the file is shorter than that.
            if (!TryParseCount(tail, out var suffix)) return null;

            start = Math.Max(0, totalSize - suffix);
            end = last;
        }
        else
        {
            if (!TryParseCount(head, out start)) return null;

            if (tail.Length == 0)
            {
                end = last;
            }
            else
            {
                if (!TryParseCount(tail, out end)) return null;

                end = Math.Min(last, end);
            }
        }

        if (start > last || end < start)
        {
            throw new DriveApiException(
                $"Range '{rangeHeader}' cannot be satisfied against {totalSize} bytes. Drive answers "
                + "416 here, and a download has no shape for that response.");
        }

        return new ByteRange(start, end);
    }

    /// <summary>
    /// <see cref="NumberStyles.None"/> on purpose: a sign, a thousands separator or surrounding
    /// whitespace inside a range is a header this does not understand, and one it does not understand
    /// is one it must ignore rather than reinterpret.
    /// </summary>
    private static bool TryParseCount(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
