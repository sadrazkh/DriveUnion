using System.Globalization;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// Every path this backend touches, and the only place a string becomes one.
///
/// The rule the whole file exists to enforce: <b>nothing a customer typed is ever a path segment.</b>
/// File names arrive from a browser and travel to <c>/d/{slug}</c> — they are Persian, they contain
/// separators, they contain <c>..</c>, they are <c>CON</c>, they are longer than any filesystem will
/// take. Sanitising such a name is a game nobody wins: <c>..</c> has at least four spellings once URL
/// and UTF-8 encoding are involved, and two customers who both upload <c>گزارش.pdf</c> must not land
/// on the same bytes. So the display name is never consulted for a location at all. It is kept in the
/// metadata record and the bytes live under a generated identifier.
///
/// Every segment produced here is therefore one of: a fixed ASCII literal, or a GUID formatted "N" —
/// thirty-two lowercase hex characters, which cannot escape a directory, cannot collide across
/// tenants, and cannot be a reserved device name. Identifiers arriving from outside are not trusted
/// either: they are parsed back into a <see cref="Guid"/> before they are allowed near a path, so a
/// hand-written <c>ld-../../secret</c> resolves to nothing rather than to something.
/// </summary>
internal static class LocalDiskLayout
{
    /// <summary>
    /// Marks an id as this backend's rather than Google's. Drive ids are opaque, so the panel does
    /// not care what shape they are — but an operator staring at a <c>StoredFile</c> row does, and a
    /// row that says <c>ld-…</c> is one whose bytes are on the box rather than at Google.
    /// </summary>
    private const string FileIdPrefix = "ld-";

    private const string FolderIdPrefix = "ldf-";

    /// <summary>
    /// The host is under <c>.invalid</c>, which is reserved by RFC 2606 and resolves nowhere. A
    /// session URI is stored in <c>UploadSession.DriveResumableUri</c> and looks exactly like the
    /// Google one it stands in for; if some future code path ever tries to send it a request, the
    /// failure is an immediate DNS error rather than a call to whoever owns the domain.
    /// </summary>
    private const string SessionUriPrefix = "https://local-disk.drive-union.invalid/uploads/";

    public static string FileId(Guid value) => FileIdPrefix + value.ToString("N", CultureInfo.InvariantCulture);

    public static string NewFolderId() => FolderIdPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static Uri SessionUri(Guid sessionId) =>
        new(SessionUriPrefix + sessionId.ToString("N", CultureInfo.InvariantCulture));

    public static bool TryParseFileId(string? fileId, out Guid value) =>
        TryParseId(fileId, FileIdPrefix, out value);

    public static bool TryParseSessionUri(Uri? sessionUri, out Guid value)
    {
        value = Guid.Empty;

        if (sessionUri is null) return false;

        var text = sessionUri.ToString();
        return text.StartsWith(SessionUriPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(text[SessionUriPrefix.Length..], "N", out value);
    }

    public static string AccountDirectory(string root, Guid accountId) =>
        Path.Combine(root, "accounts", accountId.ToString("N", CultureInfo.InvariantCulture));

    public static string FilesDirectory(string root, Guid accountId) =>
        Path.Combine(AccountDirectory(root, accountId), "files");

    /// <summary>The bytes.</summary>
    public static string ContentPath(string root, Guid accountId, Guid fileId) =>
        Path.Combine(FilesDirectory(root, accountId), fileId.ToString("N", CultureInfo.InvariantCulture) + ".bin");

    /// <summary>
    /// The name, type and size. Written only once the last chunk lands, which makes its presence the
    /// definition of "this file exists": an upload that died halfway leaves bytes nobody can reach
    /// instead of a half file the panel would offer to serve.
    /// </summary>
    public static string MetadataPath(string root, Guid accountId, Guid fileId) =>
        Path.Combine(FilesDirectory(root, accountId), fileId.ToString("N", CultureInfo.InvariantCulture) + ".json");

    public static string FolderIndexPath(string root, Guid accountId) =>
        Path.Combine(AccountDirectory(root, accountId), "folders.json");

    public static string SessionsDirectory(string root) => Path.Combine(root, "sessions");

    public static string SessionPath(string root, Guid sessionId) =>
        Path.Combine(SessionsDirectory(root), sessionId.ToString("N", CultureInfo.InvariantCulture) + ".json");

    private static bool TryParseId(string? id, string prefix, out Guid value)
    {
        value = Guid.Empty;

        return id is not null
            && id.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(id[prefix.Length..], "N", out value);
    }
}
