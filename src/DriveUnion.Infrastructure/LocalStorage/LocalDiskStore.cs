using System.Text.Json;
using DriveUnion.Core.Abstractions;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// The state files: upload sessions, file metadata, and each account's folder index.
///
/// Everything is written the same way — to a sibling <c>.tmp</c> and then moved over the target — so
/// a process that dies mid-write leaves the previous record intact rather than half a JSON document.
/// The one thing that must never be wrong here is a session's confirmed length: a torn record would
/// tell a resuming client to continue from a byte the file does not contain, and the file it
/// assembled would be corrupt in a way nothing downstream could detect.
///
/// The files are indented and camel-cased on purpose. This backend exists to be looked at while it
/// runs, and <c>cat</c> on a session record is the fastest way to see what an upload is doing.
/// </summary>
internal sealed class LocalDiskStore(string root)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<LocalUploadSessionRecord?> ReadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await ReadAsync<LocalUploadSessionRecord>(
            LocalDiskLayout.SessionPath(root, sessionId), "an upload session", cancellationToken)
            .ConfigureAwait(false);

    public async Task WriteSessionAsync(LocalUploadSessionRecord session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalDiskLayout.SessionsDirectory(root));

        await WriteAsync(LocalDiskLayout.SessionPath(root, session.SessionId), session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalFileRecord?> ReadFileAsync(
        Guid accountId,
        Guid fileId,
        CancellationToken cancellationToken) =>
        await ReadAsync<LocalFileRecord>(
            LocalDiskLayout.MetadataPath(root, accountId, fileId), "a file", cancellationToken)
            .ConfigureAwait(false);

    public async Task WriteFileAsync(
        Guid accountId,
        LocalFileRecord file,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalDiskLayout.FilesDirectory(root, accountId));

        await WriteAsync(
                LocalDiskLayout.MetadataPath(root, accountId, file.FileId), file, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalFolderIndex> ReadFoldersAsync(Guid accountId, CancellationToken cancellationToken) =>
        await ReadAsync<LocalFolderIndex>(
                LocalDiskLayout.FolderIndexPath(root, accountId), "a folder index", cancellationToken)
            .ConfigureAwait(false)
        ?? new LocalFolderIndex();

    public async Task WriteFoldersAsync(
        Guid accountId,
        LocalFolderIndex folders,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalDiskLayout.AccountDirectory(root, accountId));

        await WriteAsync(LocalDiskLayout.FolderIndexPath(root, accountId), folders, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<T?> ReadAsync<T>(string path, string what, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Loud. A state file that will not parse is a file whose bytes may still be perfectly
            // good, and quietly treating it as absent would report the upload as never having
            // happened — which is the one answer that makes the operator delete the evidence.
            throw new DriveApiException($"The local-disk record for {what} at {path} is not readable JSON.", ex);
        }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";

        await using (var stream = new FileStream(
            temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
