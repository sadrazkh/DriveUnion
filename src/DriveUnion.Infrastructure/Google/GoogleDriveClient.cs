using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Uploads;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// Drive REST v3 over a raw <see cref="HttpClient"/>.
///
/// Not the Google.Apis.Drive.v3 SDK, deliberately. Two things in this file are the product: the
/// byte-exact <c>Content-Range</c> on a resumable chunk, and the caller's <c>Range</c> forwarded to
/// Drive untouched with the response body never buffered. The SDK owns the stream lifetime and
/// buffers on both legs, which makes neither of those expressible.
/// </summary>
public sealed class GoogleDriveClient : IDriveClient, IGoogleAboutReader
{
    public const string HttpClientName = "DriveUnion.Google.Drive";

    public const string FolderMimeType = "application/vnd.google-apps.folder";

    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string ResumableUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string AboutEndpoint = "https://www.googleapis.com/drive/v3/about";

    /// <summary>
    /// Asked for on the session-initiation URL, because a resumable session answers its completion
    /// with the query it was opened with. Without it Drive returns id, name and mimeType only, and
    /// the size we would then record is our own claim rather than Drive's.
    /// </summary>
    private const string FileFields = "id,name,mimeType,size,createdTime,modifiedTime";

    /// <summary>
    /// Google documents resumable sessions as lasting about a week. The exact window is theirs and
    /// they can change it, so this value is bookkeeping only — the authority on whether a session is
    /// still alive is Drive's answer to a probe, which is why a dead one surfaces as
    /// <see cref="DriveUploadSessionExpiredException"/> rather than being predicted from this clock.
    /// </summary>
    private static readonly TimeSpan ResumableSessionLifetime = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly IGoogleTokenService _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleDriveClient> _logger;

    public GoogleDriveClient(
        HttpClient http,
        IGoogleTokenService tokens,
        TimeProvider timeProvider,
        ILogger<GoogleDriveClient> logger)
    {
        _http = http;
        _tokens = tokens;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = QueryHelpers.AddQueryString(
            ResumableUploadEndpoint,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["uploadType"] = "resumable",
                ["fields"] = FileFields,
            });

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                BuildFileMetadata(request.FileName, request.MimeType, request.ParentFolderId),
                Encoding.UTF8,
                "application/json"),
        };

        await AuthorizeAsync(message, accountId, cancellationToken).ConfigureAwait(false);

        // Not required, but they let Drive reject a mismatched size at initiation rather than after
        // the last chunk has already crossed the Atlantic.
        message.Headers.TryAddWithoutValidation("X-Upload-Content-Type", request.MimeType);
        message.Headers.TryAddWithoutValidation(
            "X-Upload-Content-Length",
            request.SizeBytes.ToString(CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "opening a resumable upload session", cancellationToken)
            .ConfigureAwait(false);

        var location = response.Headers.Location
            ?? throw new DriveApiException(
                "Drive accepted the resumable upload initiation but returned no Location header, so "
                + "there is no session to write into.");

        return new DriveResumableSession(
            location.IsAbsoluteUri ? location : new Uri(new Uri(ResumableUploadEndpoint), location),
            _timeProvider.GetUtcNow() + ResumableSessionLifetime);
    }

    public async Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);
        ArgumentNullException.ThrowIfNull(content);

        if (!UploadChunking.IsValidChunk(offset, length, totalSize))
        {
            throw new DriveApiException(
                $"Refusing to send {length} bytes at offset {offset} of {totalSize}. Drive would not "
                + "reject this — it would accept the request and quietly stop acknowledging bytes.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Put, sessionUri)
        {
            Content = new ForwardedStreamContent(content, length),
        };

        // The session URI is itself the capability — it carries its own upload_id, which is why this
        // method takes no account id and no bearer token can be attached here.
        message.Content.Headers.TryAddWithoutValidation(
            "Content-Range",
            UploadChunking.ContentRange(offset, length, totalSize));

        // See DriveRetryHandler.NonRewindableBody: a replay of this request sends nothing at all.
        message.Options.Set(DriveRetryHandler.NonRewindableBody, true);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (IsResumeIncomplete(response))
        {
            return new DriveChunkOutcome(ReadConfirmedLength(response), Completed: null);
        }

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new DriveChunkOutcome(totalSize, ParseFileMetadata(body, totalSize));
        }

        ThrowIfSessionGone(response, sessionUri);
        await EnsureSuccessAsync(response, "writing an upload chunk", cancellationToken).ConfigureAwait(false);

        // EnsureSuccessAsync throws for every non-2xx, and 2xx was handled above; reaching here means
        // Drive answered with a success code nobody has seen before.
        throw new DriveApiException(
            $"Drive answered {(int)response.StatusCode} to a chunk write, which is neither a "
            + "completion nor a resume-incomplete.");
    }

    public async Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionUri);

        using var message = new HttpRequestMessage(HttpMethod.Put, sessionUri)
        {
            Content = new ByteArrayContent([]),
        };

        message.Content.Headers.ContentLength = 0;
        message.Content.Headers.TryAddWithoutValidation(
            "Content-Range",
            UploadChunking.ProbeContentRange(totalSize));

        // No opt-out here on purpose: the body is empty, so this one is safe to replay.
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (IsResumeIncomplete(response))
        {
            return ReadConfirmedLength(response);
        }

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            // The upload finished; the client and our row simply had not caught up.
            return totalSize;
        }

        ThrowIfSessionGone(response, sessionUri);
        await EnsureSuccessAsync(response, "probing an upload session", cancellationToken).ConfigureAwait(false);

        throw new DriveApiException(
            $"Drive answered {(int)response.StatusCode} to an upload probe instead of 308.");
    }

    public async Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveFileId);

        var url = $"{FilesEndpoint}/{Uri.EscapeDataString(driveFileId)}?alt=media";

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        await AuthorizeAsync(message, accountId, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            // Verbatim, unparsed. Drive owns the semantics of multipart ranges, suffix ranges and
            // open-ended ranges; re-deriving them here would only add a place to get them wrong.
            message.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await EnsureSuccessAsync(response, $"downloading file {driveFileId}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        // ReadAsStreamAsync on a ResponseHeadersRead response hands back the live network stream.
        // Nothing here may read from it: the caller copies it to the wire, and a 214 GB file has to
        // cost a buffer rather than a copy.
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return new DriveDownload(
            stream,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            response.Content.Headers.ContentLength,
            FirstHeaderValue(response.Content.Headers, "Content-Range"),
            response.StatusCode == HttpStatusCode.PartialContent,
            new ResponseOwner(response));
    }

    public async Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        var existing = await FindFolderAsync(accountId, folderName, parentFolderId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            QueryHelpers.AddQueryString(FilesEndpoint, "fields", "id"))
        {
            Content = new StringContent(
                BuildFileMetadata(folderName, FolderMimeType, parentFolderId),
                Encoding.UTF8,
                "application/json"),
        };

        await AuthorizeAsync(message, accountId, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"creating the folder {folderName}", cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseOrThrow(body, "the folder Drive just created");

        var id = document.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            throw new DriveApiException($"Drive created the folder {folderName} but returned no id.");
        }

        _logger.LogInformation(
            "Created Drive folder {FolderName} ({FolderId}) in account {AccountId}.",
            folderName,
            id,
            accountId);

        return id;
    }

    public async Task<DriveStorageQuota> GetStorageQuotaAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(AboutEndpoint, "fields", "storageQuota"));

        await AuthorizeAsync(message, accountId, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "reading the storage quota", cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (limit, usage) = ParseStorageQuota(body);
        return new DriveStorageQuota(limit, usage);
    }

    public async Task<GoogleAboutInfo> GetAboutAsync(string accessToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(AboutEndpoint, "fields", "user,storageQuota"));

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "reading the account's identity and quota", cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseOrThrow(body, "the about resource");

        var hasUser = document.RootElement.TryGetProperty("user", out var user);

        var email = hasUser && user.TryGetProperty("emailAddress", out var address)
            ? address.GetString()
            : null;

        if (string.IsNullOrEmpty(email))
        {
            throw new DriveApiException(
                "Drive returned no email address for this account. The account cannot be stored "
                + "without one — it is what the operator reads on the card.");
        }

        // Already in the response: the request asks for `fields=user`, and permissionId is part of
        // the user resource. Reading it costs nothing and needs no extra scope, which is the reason
        // it is preferred over the id token's `sub` — that would mean asking for `openid` and
        // widening a consent screen the operator already has to click through an unverified-app
        // warning to reach.
        var permissionId = hasUser && user.TryGetProperty("permissionId", out var id)
            ? id.GetString()
            : null;

        var (limit, usage) = ParseStorageQuota(body);
        return new GoogleAboutInfo(email, permissionId, limit, usage);
    }

    private async Task<string?> FindFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        var parent = parentFolderId ?? "root";
        var query =
            $"name = '{EscapeQueryLiteral(folderName)}' and mimeType = '{FolderMimeType}' "
            + $"and '{EscapeQueryLiteral(parent)}' in parents and trashed = false";

        var url = QueryHelpers.AddQueryString(
            FilesEndpoint,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["q"] = query,
                ["fields"] = "files(id)",
                ["pageSize"] = "1",
                ["spaces"] = "drive",
            });

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        await AuthorizeAsync(message, accountId, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"looking for the folder {folderName}", cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseOrThrow(body, "a folder listing");

        if (!document.RootElement.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private async Task AuthorizeAsync(
        HttpRequestMessage message,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync(accountId, cancellationToken).ConfigureAwait(false);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>308 in a resumable upload means "resume incomplete", not "moved permanently".</summary>
    private static bool IsResumeIncomplete(HttpResponseMessage response) =>
        (int)response.StatusCode == 308;

    /// <summary>
    /// The confirmed prefix, read from the <c>Range</c> <em>response</em> header: <c>bytes=0-262143</c>
    /// means 262,144 bytes are safely stored. Drive omits the header entirely when it holds nothing,
    /// which is zero rather than an error.
    /// </summary>
    private static long ReadConfirmedLength(HttpResponseMessage response)
    {
        var value = FirstHeaderValue(response.Headers, "Range");
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var separator = value.IndexOf('=', StringComparison.Ordinal);
        var ranges = separator >= 0 ? value[(separator + 1)..] : value;

        // Drive sends one contiguous range anchored at zero; taking the last one is defensive only.
        var last = ranges.Split(',')[^1];
        var dash = last.LastIndexOf('-');
        if (dash < 0)
        {
            return 0;
        }

        return long.TryParse(
            last[(dash + 1)..].Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var end)
            ? end + 1
            : 0;
    }

    private static void ThrowIfSessionGone(HttpResponseMessage response, Uri sessionUri)
    {
        // Google documents 404 for a session that has expired or been cancelled. 410 is treated the
        // same way defensively: both mean the URI is gone and no amount of retrying revives it.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new DriveUploadSessionExpiredException(
                $"Drive no longer recognises this upload session ({(int)response.StatusCode}). "
                + "Sessions last about a week; this one has to be started over. "
                + $"Host: {sessionUri.Host}");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var error = GoogleApiError.Parse(body);

        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && error.IsRateLimit))
        {
            // The retry handler has already spent its budget by the time this is reached — unless
            // the request was a chunk write, which is never retried at all. Either way the caller
            // has to decide, and for a chunk that means re-probing the session and sending again.
            throw new DriveRateLimitedException(
                $"Google rate-limited {what}: {error.Describe()}",
                response.Headers.RetryAfter?.Delta);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new DriveAccountUnavailableException(
                $"Google rejected the credentials while {what}: {error.Describe()}");
        }

        throw new DriveApiException($"Google answered {(int)response.StatusCode} while {what}: {error.Describe()}");
    }

    private static async Task<string?> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // The failure being reported is the interesting one; a body that will not read is not.
            return null;
        }
    }

    private static string BuildFileMetadata(string name, string mimeType, string? parentId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("mimeType", mimeType);

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                writer.WriteStartArray("parents");
                writer.WriteStringValue(parentId);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private DriveFileMetadata ParseFileMetadata(string body, long uploadedSize)
    {
        using var document = ParseOrThrow(body, "the completed file's metadata");
        var root = document.RootElement;

        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            throw new DriveApiException(
                "Drive reported the upload complete but returned no file id, so there is nothing to "
                + "record and nothing to serve.");
        }

        // Everything below id is a fallback, because the fields Drive returns depend on the query
        // the session was opened with and that is one round trip away from where the failure shows.
        var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var mimeType = root.TryGetProperty("mimeType", out var mimeValue) ? mimeValue.GetString() : null;

        return new DriveFileMetadata(
            id,
            name ?? string.Empty,
            mimeType ?? "application/octet-stream",
            ReadInt64(root, "size") ?? uploadedSize,
            ReadTimestamp(root, "createdTime") ?? _timeProvider.GetUtcNow(),
            ReadTimestamp(root, "modifiedTime") ?? _timeProvider.GetUtcNow());
    }

    private static (long Limit, long Usage) ParseStorageQuota(string body)
    {
        using var document = ParseOrThrow(body, "the storage quota");

        if (!document.RootElement.TryGetProperty("storageQuota", out var quota))
        {
            throw new DriveApiException("Drive's about resource carried no storageQuota.");
        }

        // `usage` rather than `usageInDrive`: what counts against a Google One plan is Drive, Gmail
        // and Photos together, and that total is the number the operator's dashboard has to show.
        //
        // `limit` is absent when an account has unlimited storage. Consumer Google One always states
        // one, so a zero here means Drive answered something new — it is reported as zero rather
        // than assumed to be infinite, because guessing wrong fills an account M2 then routes to.
        return (ReadInt64(quota, "limit") ?? 0, ReadInt64(quota, "usage") ?? 0);
    }

    /// <summary>
    /// Drive sends 64-bit values as JSON strings. Both shapes are accepted because the string form
    /// is a documented quirk rather than a guarantee.
    /// </summary>
    private static long? ReadInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static JsonDocument ParseOrThrow(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new DriveApiException($"Drive returned something that is not JSON for {what}.", ex);
        }
    }

    /// <summary>
    /// Read raw rather than through the typed header properties: <c>Content-Range</c> is mirrored
    /// back to the client and a re-serialised header is not necessarily the same string.
    /// </summary>
    private static string? FirstHeaderValue(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Drive's query language quotes literals with single quotes and escapes with a backslash. An
    /// unescaped apostrophe in a tenant slug would not fail — it would change the query.
    /// </summary>
    private static string EscapeQueryLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    /// <summary>
    /// Keeps the response alive for exactly as long as the stream taken out of it.
    /// <see cref="DriveDownload"/> wants an <see cref="IAsyncDisposable"/> and
    /// <see cref="HttpResponseMessage"/> is not one.
    /// </summary>
    private sealed class ResponseOwner(HttpResponseMessage response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
