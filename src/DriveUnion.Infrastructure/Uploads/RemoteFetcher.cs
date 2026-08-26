using System.Net;
using System.Net.Http.Headers;
using DriveUnion.Core.Application;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Pulls a file from somewhere else on the internet straight into storage.
///
/// <para><b>Nothing is held.</b> The response body is read a chunk at a time and each chunk goes
/// into the same resumable upload an ordinary browser upload uses — so a 40 GB fetch costs this
/// server a buffer, not 40 GB of disk it does not have. The whole feature exists to keep the file
/// off the customer's connection, and putting it on the operator's disk instead would be trading one
/// bottleneck for a worse one.</para>
///
/// <para><b>It goes through <see cref="IUploadCoordinator"/> and not around it.</b> That is where
/// the plan's per-file ceiling is enforced, where the workspace's quota is reserved, where the file
/// lands in the right person's folder and where the catalogue row is written. A fetch that talked to
/// Drive directly would be a second upload path with its own copies of all four, and the second copy
/// is the one that ends up wrong.</para>
///
/// <para><b>Every connection goes through <c>GuardedFetchHandler</c>.</b> This is the one place in
/// the product where a customer chooses an address the server dials, and the check happens at
/// connect time — see that class for the window it closes.</para>
/// </summary>
public sealed class RemoteFetcher(
    DriveUnionDbContext db,
    IUploadCoordinator uploads,
    IHttpClientFactory http,
    TimeProvider clock,
    ILogger<RemoteFetcher> logger) : IRemoteFetcher
{
    /// <summary>The named client wired to <see cref="GuardedFetchHandler"/>. Nothing else may fetch.</summary>
    public const string ClientName = "remote-fetch";

    /// <summary>
    /// What is read from the source and written to storage in one go.
    ///
    /// <para>8 MiB, the same as the account migrator's, and for the same reason: this runs
    /// unattended alongside everything else the server is doing, so the memory it occupies has to
    /// stay bounded whatever else is happening.</para>
    /// </summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    private const int CopyBuffer = 81920;

    /// <summary>
    /// How long the whole pull may take.
    ///
    /// <para>Six hours. Long enough for a very large file over a slow source and short enough that a
    /// server which accepts a connection and then sends one byte an hour eventually lets go.</para>
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromHours(6);

    public async Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken)
    {
        var done = 0;

        for (var i = 0; i < budget && !cancellationToken.IsCancellationRequested; i++)
        {
            var fetch = await NextAsync(cancellationToken);
            if (fetch is null) break;

            if (await PullAsync(fetch, cancellationToken)) done++;
        }

        return done;
    }

    /// <summary>The oldest queued fetch. One at a time, so the operator's line is not the bottleneck.</summary>
    private async Task<RemoteFetch?> NextAsync(CancellationToken cancellationToken)
    {
        var queued = await db.RemoteFetches
            .Where(f => f.Status == RemoteFetchStatus.Queued)
            .ToListAsync(cancellationToken);

        // In memory for the DateTimeOffset reason above.
        return queued.OrderBy(f => f.CreatedAt).FirstOrDefault();
    }

    private async Task<bool> PullAsync(RemoteFetch fetch, CancellationToken cancellationToken)
    {
        fetch.Status = RemoteFetchStatus.Running;
        fetch.Attempts++;
        await db.SaveChangesAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        try
        {
            await TransferAsync(fetch, deadline.Token);

            fetch.Status = RemoteFetchStatus.Completed;
            fetch.FinishedAt = clock.GetUtcNow();
            fetch.FailureReason = null;

            await db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping. Back to Queued so it is picked up next time rather than counted
            // as a failure the customer has to do something about.
            fetch.Status = RemoteFetchStatus.Queued;
            fetch.Attempts--;

            await db.SaveChangesAsync(CancellationToken.None);

            return false;
        }
        catch (Exception exception)
        {
            await FailAsync(fetch, Describe(exception), exception, Permanent(exception));

            return false;
        }
    }

    /// <summary>
    /// Whether trying again could possibly help.
    ///
    /// <para>An address on the refused list will still be on it in a minute, and a scheme that is
    /// not http will still not be. Retrying those is three DNS lookups and two more minutes before
    /// the customer is told the same thing — so they fail once and say so.</para>
    /// </summary>
    private static bool Permanent(Exception exception) =>
        Unwrap<RemoteAddressRefusedException>(exception) is not null
        || exception is UploadRejectedException
        || (exception as RemoteFetchRefusedException)?.Refusal
            is RemoteSourceRefusal.LengthUnknown
            or RemoteSourceRefusal.UnsupportedScheme
            or RemoteSourceRefusal.CarriesCredentials;

    /// <summary>
    /// The exception of this type in the chain, or null.
    ///
    /// <para><c>HttpClient</c> wraps whatever a connect callback throws in an
    /// <c>HttpRequestException</c>, so the type that says «this address is refused» never arrives at
    /// the top. Without this the customer is told the source could not be reached — which is true of
    /// a host that is merely down, and is the wrong sentence for one they are not allowed to ask
    /// for.</para>
    /// </summary>
    private static T? Unwrap<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match) return match;
        }

        return null;
    }

    private async Task TransferAsync(RemoteFetch fetch, CancellationToken cancellationToken)
    {
        var client = http.CreateClient(ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, fetch.Url);

        // Headers first: the length and the type decide whether this is worth starting, and reading
        // them before the body is what lets a file over the plan's ceiling be refused without
        // pulling a single byte of it.
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteFetchRefusedException(
                RemoteSourceRefusal.Unreachable,
                $"The source answered {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is not { } length || length <= 0)
        {
            // Storage needs a total before it will open a resumable session, and a source that will
            // not say how big a file is cannot be given one. Said plainly rather than started and
            // abandoned halfway.
            throw new RemoteFetchRefusedException(
                RemoteSourceRefusal.LengthUnknown,
                "The source would not say how big the file is.");
        }

        fetch.FileName = NameFor(fetch.Url, response.Content.Headers.ContentDisposition);
        fetch.SizeBytes = length;
        await db.SaveChangesAsync(cancellationToken);

        // The coordinator refuses here if the plan's per-file ceiling or the workspace's quota says
        // no — before a byte of the body is read.
        var begun = await uploads.BeginAsync(
            fetch.TenantId,
            fetch.OwnerUserId,
            new BeginUploadRequest(
                fetch.FileName,
                response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                length),
            cancellationToken);

        fetch.UploadSessionId = begun.SessionId;
        await db.SaveChangesAsync(cancellationToken);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[ChunkSize];
        var sent = 0L;

        while (sent < length)
        {
            // Cancelled by the customer mid-pull. Checked between chunks rather than only at the
            // start, because a chunk is eight megabytes and a 40 GB fetch is five thousand of them.
            if (await IsCancelledAsync(fetch.Id, cancellationToken))
            {
                throw new OperationCanceledException("The customer stopped this fetch.");
            }

            var wanted = (int)Math.Min(ChunkSize, length - sent);
            var filled = await ReadExactlyAsync(body, buffer, wanted, cancellationToken);

            if (filled < wanted)
            {
                // Short of what was asked for means the body ended, which means the source lied
                // about its length. Thrown here rather than written: a resumable upload takes a
                // partial chunk only as its last one, so sending this would be refused by storage
                // for a reason that reads like a protocol bug instead of «the far end stopped».
                throw new RemoteFetchRefusedException(
                    RemoteSourceRefusal.Unreachable,
                    $"The source stopped after {sent + filled} of {length} bytes.");
            }

            using var chunk = new MemoryStream(buffer, 0, filled, writable: false);

            var progress = await uploads.WriteChunkAsync(
                fetch.TenantId, begun.SessionId, chunk, sent, filled, cancellationToken);

            sent += filled;

            fetch.BytesFetched = progress.BytesConfirmed;
            fetch.StoredFileId = progress.StoredFileId;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (fetch.StoredFileId is null)
        {
            throw new RemoteFetchRefusedException(
                RemoteSourceRefusal.Unreachable,
                "Every byte was written and storage never completed the file.");
        }
    }

    /// <summary>
    /// Whether the customer has stopped this one, read fresh from the database.
    ///
    /// <para>Fresh and not from the tracked entity: the cancellation was written by a web request in
    /// another scope, and the copy this worker is holding was loaded before it happened.</para>
    /// </summary>
    private async Task<bool> IsCancelledAsync(Guid fetchId, CancellationToken cancellationToken) =>
        await db.RemoteFetches
            .AsNoTracking()
            .Where(f => f.Id == fetchId)
            .Select(f => f.Status)
            .FirstOrDefaultAsync(cancellationToken) == RemoteFetchStatus.Cancelled;

    private async Task FailAsync(RemoteFetch fetch, string reason, Exception exception, bool permanent)
    {
        fetch.FailureReason = reason.Length <= RemoteFetch.MaxFailureReasonLength
            ? reason
            : reason[..RemoteFetch.MaxFailureReasonLength];

        if (permanent || fetch.Attempts >= RemoteFetch.MaxAttempts)
        {
            fetch.Status = RemoteFetchStatus.Failed;
            fetch.FinishedAt = clock.GetUtcNow();

            logger.LogWarning(
                exception,
                "Gave up fetching remote file {FetchId} after {Attempts} attempts.",
                fetch.Id,
                fetch.Attempts);
        }
        else
        {
            // Back in the queue. A source that is down for a minute is worth another go.
            fetch.Status = RemoteFetchStatus.Queued;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// What to say to the customer.
    ///
    /// <para>Never the exception's own message for anything that came off a socket: it names this
    /// server's DNS, its addresses and its stack. What a customer gets is which of the things that
    /// can go wrong went wrong.</para>
    /// </summary>
    private static string Describe(Exception exception)
    {
        // Asked for by unwrapping rather than by matching the top of the chain: HttpClient wraps
        // whatever a connect callback throws, so the one refusal that is genuinely about the
        // customer's choice of address would otherwise arrive dressed as a network failure.
        if (Unwrap<RemoteAddressRefusedException>(exception) is not null)
        {
            return "That address is not one this server will fetch from.";
        }

        return exception switch
        {
            RemoteFetchRefusedException refused => refused.Message,
            UploadRejectedException rejected => rejected.Message,
            TaskCanceledException or TimeoutException => "The source took too long.",
            HttpRequestException => "The source could not be reached.",
            _ => "The file could not be fetched.",
        };
    }

    /// <summary>
    /// What the file will be called.
    ///
    /// <para><c>Content-Disposition</c> first, because it is the only thing that is actually a
    /// filename; then the URL's last path segment, which usually is one; then a name we make up,
    /// because a file with no name is worse than a file with a dull one.</para>
    ///
    /// <para>Everything that could make it a path is taken out. A <c>filename</c> of
    /// <c>../../etc/passwd</c> is a real thing servers send, and while nothing downstream here joins
    /// it onto a path, the name is stored, shown and put into a Content-Disposition of our own.</para>
    /// </summary>
    public static string NameFor(string url, ContentDispositionHeaderValue? disposition)
    {
        var stated = disposition?.FileNameStar ?? disposition?.FileName;

        var candidate = Sanitise(stated)
            ?? Sanitise(LastSegment(url))
            ?? $"fetched-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

        return candidate.Length <= RemoteFetch.MaxFileNameLength
            ? candidate
            : candidate[..RemoteFetch.MaxFileNameLength];
    }

    private static string? LastSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');

        var segment = slash >= 0 ? path[(slash + 1)..] : path;

        return WebUtility.UrlDecode(segment);
    }

    private static string? Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Quotes are how the header carries it and are not part of the name.
        var trimmed = name.Trim().Trim('"');

        // Directory separators and the traversal they enable, then anything a filesystem refuses.
        var cleaned = new string([.. trimmed
            .Where(c => c is not ('/' or '\\' or ':'))
            .Where(c => !char.IsControl(c))]);

        cleaned = cleaned.Replace("..", string.Empty, StringComparison.Ordinal).Trim();

        return cleaned.Length > 0 ? cleaned : null;
    }

    private static async Task<int> ReadExactlyAsync(
        Stream source,
        byte[] buffer,
        int wanted,
        CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < wanted)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(filled, Math.Min(CopyBuffer, wanted - filled)),
                cancellationToken);

            if (read == 0) break;

            filled += read;
        }

        return filled;
    }
}

/// <summary>A refusal with a sentence already fit to show a customer.</summary>
public sealed class RemoteFetchRefusedException(RemoteSourceRefusal refusal, string message)
    : Exception(message)
{
    public RemoteSourceRefusal Refusal { get; } = refusal;
}
