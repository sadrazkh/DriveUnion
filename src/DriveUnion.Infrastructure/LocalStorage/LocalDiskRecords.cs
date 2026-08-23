namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// An open resumable upload, as it survives a restart.
///
/// <see cref="ConfirmedLength"/> is the whole point of the record. It is written only after the bytes
/// it counts are on the file, so it means the same thing Drive's <c>Range</c> response header means:
/// this many bytes are safely stored, resume from here. A crash between the copy and this record
/// costs the last chunk and nothing else — the client sends it again and overwrites the tail.
/// </summary>
internal sealed class LocalUploadSessionRecord
{
    public Guid SessionId { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>
    /// Chosen when the session opens, not when it completes, so the chunks can stream straight into
    /// their final location. Drive only reveals its id at the end, but that is a fact about its API
    /// and not one anything above <c>IDriveClient</c> can observe.
    /// </summary>
    public Guid FileId { get; set; }

    /// <summary>The customer's name for the file, verbatim. Never used to build a path.</summary>
    public string FileName { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string? ParentFolderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public long ConfirmedLength { get; set; }

    public bool Completed { get; set; }
}

/// <summary>
/// What a finished file is. Its presence next to the bytes is what makes them a file — see
/// <see cref="LocalDiskLayout.MetadataPath"/>.
/// </summary>
internal sealed class LocalFileRecord
{
    public Guid FileId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }

    public string? ParentFolderId { get; set; }
}

internal sealed class LocalFolderRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ParentFolderId { get; set; }
}

/// <summary>
/// The account's folder tree, all of it, in one file.
///
/// Folders here are labels rather than directories: no byte's location depends on one, because the
/// bytes live flat under generated identifiers. That is what lets a folder be named anything a
/// customer can type without any of it reaching the filesystem.
/// </summary>
internal sealed class LocalFolderIndex
{
    public List<LocalFolderRecord> Folders { get; set; } = [];
}
