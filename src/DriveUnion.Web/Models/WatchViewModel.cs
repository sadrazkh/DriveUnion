namespace DriveUnion.Web.Models;

/// <summary>
/// A screen whose only job is playing one file.
///
/// <para><b>Why this is a page and not a box on another one.</b> The player used to be drawn inside
/// the file detail panel and inside the public download card — both of which are narrow columns full
/// of other things, and on a phone both became a stack of controls with a video wedged into it. A
/// film is the thing somebody came for; it should not be the fourth item in a sidebar.</para>
///
/// <para>It also gives the locked case somewhere to happen. Unlocking is a passphrase, a wait, and
/// then a player — three states that need room, and that were previously crammed under a summary
/// row. Here the page is the process.</para>
///
/// <para>One model for both the panel's copy and the public one. What differs between them is where
/// the bytes are and where «back» goes, and those are two fields rather than two screens.</para>
/// </summary>
/// <param name="ContentUrl">
/// Where the bytes are — the owner's metered route, or the public link's download address.
///
/// <para>For a locked file this is ciphertext, and the service worker reads it a segment at a time.
/// Nothing is requested until the reader presses play or unlocks, so opening this page costs
/// nothing.</para>
/// </param>
/// <param name="EncryptionJson">
/// The header, for a locked file, and null for one that is not. Its presence is what decides whether
/// the page asks for a passphrase before it shows a player.
/// </param>
public sealed record WatchViewModel(
    string Title,
    string SizeText,
    string KindText,

    /// <summary>'video' or 'audio'. The page is not rendered for anything else.</summary>
    string MediaKind,

    /// <summary>
    /// The recorded type, which the element and the worker both need.
    ///
    /// <para>The real one — <c>video/mp4</c> — and never the display string beside it. The panel
    /// passed <c>KindText</c> here once, so the worker answered with a Content-Type of «MP4» and
    /// every locked film failed to decode with nothing saying why.</para>
    /// </summary>
    string MimeType,

    string ContentUrl,
    string? EncryptionJson,
    string BackUrl,
    string BackText)
{
    public bool IsLocked => EncryptionJson is { Length: > 0 };

    public bool IsVideo => MediaKind == "video";
}
