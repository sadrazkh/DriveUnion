namespace DriveUnion.Core.Sharing;

/// <summary>
/// What counts as a download.
///
/// The naive answer — every request to <c>/d/{slug}/file</c> — bills a customer twenty downloads
/// out of their five hundred because one viewer scrubbed through a video. Every seek is its own
/// ranged request. So: count a request that asks for the whole file, or one that starts at byte 0.
/// A continuation is somebody finishing what they already started.
///
/// The <c>Range</c> header itself is forwarded to Drive verbatim and Drive's answer is mirrored
/// back; nothing here needs to understand range semantics beyond where the first byte sits.
/// </summary>
public static class DownloadCounting
{
    public static bool CountsAsDownload(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return true;

        var value = rangeHeader.Trim();
        const string unitPrefix = "bytes=";
        if (!value.StartsWith(unitPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A unit we do not model. Drive decides whether to honour it; we count it once rather
            // than let an unrecognised header become a free download.
            return true;
        }

        // Multipart ranges are judged by their first spec — if a client asks for the head of the
        // file plus other pieces, it is starting a download.
        var firstSpec = value[unitPrefix.Length..].Split(',')[0].Trim();

        var dash = firstSpec.IndexOf('-');
        if (dash <= 0)
        {
            // "-500" is a suffix range (the last 500 bytes) and "" is malformed. Neither begins a
            // download, and a malformed range is Drive's 416 to issue, not ours to bill for.
            return false;
        }

        if (!long.TryParse(firstSpec[..dash], out var firstByte) || firstByte != 0) return false;

        // `bytes=0-0` is the one-byte probe a <video> element sends to discover the length and
        // whether ranges are supported. It starts at zero and is not a download — counting it bills
        // every playback twice, because the real request follows immediately behind it.
        var last = firstSpec[(dash + 1)..].Trim();
        return last != "0";
    }
}
