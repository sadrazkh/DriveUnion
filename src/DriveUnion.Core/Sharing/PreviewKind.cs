namespace DriveUnion.Core.Sharing;

/// <summary>
/// What the public page may draw for a file, and nothing more.
/// </summary>
public enum PreviewKind
{
    /// <summary>The striped placeholder. Everything that is not on the list below.</summary>
    None = 0,
    Image = 1,
    Video = 2,
    Audio = 3,
    Document = 4,
}

/// <summary>
/// Whether a file may be rendered in a page on this origin, and as what.
///
/// <para><b>An allow-list, and it has to be.</b> The inline route this feeds sends
/// <c>Content-Disposition: inline</c>, which is the whole difference between a browser saving a file
/// and running it. <c>text/html</c> and <c>image/svg+xml</c> served that way are a script executing
/// on this product's own origin, against a session that belongs to whoever opened the link — so the
/// question is never "is this type dangerous?" but "is this one of the few we have decided about?".
/// A list of the forbidden is a list somebody has to keep complete; this one only has to stay
/// short.</para>
///
/// <para>Deliberately absent: <c>text/plain</c>, which browsers will happily sniff into something
/// else, and every <c>application/*</c> but PDF. <c>image/svg+xml</c> is absent for the reason
/// above, even though it is an image and would otherwise belong.</para>
/// </summary>
public static class Previews
{
    /// <summary>
    /// Past this the page draws the placeholder and offers the button instead.
    ///
    /// <para>Not a limit on the file — a 200 GB video is what this product is for. It is a limit on
    /// what may be poured into a page nobody has asked to download yet, and it exists for a reason
    /// worth writing down: <b>a preview does not spend a download.</b></para>
    ///
    /// <para>It cannot. A link capped at five downloads would be empty after five page loads, and a
    /// player asking for one byte of metadata is not somebody taking the file. But that leaves a
    /// route that serves bytes without touching the counter, so the counter is not the thing holding
    /// it: this ceiling is. Twenty-five megabytes is what a capped link can leak per request instead
    /// of two hundred gigabytes, and once the cap is actually reached the link stops resolving at
    /// all — previews included — because the same <c>Evaluate</c> governs both.</para>
    ///
    /// <para>The whole file is still metered as egress either way. The operator's books are right
    /// even where the customer's cap is deliberately not consulted.</para>
    /// </summary>
    public const long MostBytesToShowWhole = 25 * 1024 * 1024;

    private static readonly string[] Images =
        ["image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp"];

    private static readonly string[] Videos = ["video/mp4", "video/webm", "video/ogg"];

    private static readonly string[] Audios =
        ["audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/webm", "audio/flac"];

    /// <param name="mimeType">
    /// The type recorded at upload, which came from the browser and is not to be trusted as a fact
    /// about the bytes. It is trusted as an <i>intent</i>, and that is enough here: the inline route
    /// serves the same string back, so a file claiming to be a PNG is served as a PNG and rendered as
    /// one. What it must never do is name a type that could execute, which is what the list is for.
    /// </param>
    /// <param name="sizeBytes">What is stored. See <see cref="MostBytesToShowWhole"/>.</param>
    /// <param name="isEncrypted">
    /// Ends the question. The bytes are ciphertext, so every one of these would render as a broken
    /// image or a player that fails — the placeholder is not a worse answer, it is the true one.
    /// </param>
    public static PreviewKind For(string? mimeType, long sizeBytes, bool isEncrypted)
    {
        if (isEncrypted || sizeBytes > MostBytesToShowWhole) return PreviewKind.None;

        return OfType(mimeType);
    }

    /// <summary>
    /// Whether this exact type may be sent with an inline disposition, ignoring size.
    ///
    /// <para>Size is the caller's to check, and the route does check it. This one answers the
    /// question the disposition actually turns on — can this type execute in a page on our
    /// origin — so that the two are not accidentally the same test.</para>
    /// </summary>
    public static bool MayBeInline(string? mimeType) => OfType(mimeType) != PreviewKind.None;

    private static PreviewKind OfType(string? mimeType)
    {
        var type = Normalise(mimeType);

        if (type.Length == 0) return PreviewKind.None;
        if (Images.Contains(type)) return PreviewKind.Image;
        if (Videos.Contains(type)) return PreviewKind.Video;
        if (Audios.Contains(type)) return PreviewKind.Audio;
        if (type == "application/pdf") return PreviewKind.Document;

        return PreviewKind.None;
    }

    /// <summary>
    /// The type without its parameters, folded.
    ///
    /// <para><c>text/html; charset=utf-8</c> must not slip past a list of bare types because of the
    /// half after the semicolon, and <c>IMAGE/PNG</c> is the same type as <c>image/png</c>.</para>
    /// </summary>
    private static string Normalise(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return string.Empty;

        var semicolon = mimeType.IndexOf(';');
        var bare = semicolon < 0 ? mimeType : mimeType[..semicolon];

        return bare.Trim().ToLowerInvariant();
    }
}
