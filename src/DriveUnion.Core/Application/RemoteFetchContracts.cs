using DriveUnion.Core.Uploads;

namespace DriveUnion.Core.Application;

/// <param name="Refusal">
/// Why it was not accepted, or <c>None</c>. Every value is something the customer can act on, which
/// is why this is an answer rather than an exception.
/// </param>
public sealed record RemoteFetchStartResult(Guid? FetchId, RemoteSourceRefusal Refusal, string? Detail = null)
{
    public bool Started => FetchId is not null;
}

/// <summary>One queued or finished fetch, as the upload screen shows it.</summary>
public sealed record RemoteFetchView(
    Guid Id,
    string Url,
    string? FileName,
    RemoteFetchStatus Status,
    long SizeBytes,
    long BytesFetched,
    string? FailureReason,
    DateTimeOffset CreatedAt);

/// <summary>
/// The customer's side of «go and get this for me».
///
/// <para>Queue, watch, cancel. Nothing here fetches anything — that is the worker's, because a
/// 40 GB pull is not something a form post waits for and the browser that asked for it is expected
/// to be closed long before it finishes.</para>
/// </summary>
public interface IRemoteFetches
{
    /// <summary>
    /// Queues a URL, or says why not.
    ///
    /// <para>The URL's own shape is checked here and refused immediately — a customer who typed
    /// <c>file:///etc/passwd</c> should be told now rather than by a job that fails in a minute. The
    /// address it resolves to is <b>not</b> checked here, and deliberately: that check belongs at
    /// connect time, or a name can answer differently between the two moments.</para>
    /// </summary>
    Task<RemoteFetchStartResult> StartAsync(
        Guid tenantId,
        Guid? ownerUserId,
        string url,
        CancellationToken cancellationToken);

    /// <summary>This workspace's fetches, newest first.</summary>
    Task<IReadOnlyList<RemoteFetchView>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Stops one. What has already been written stays written.</summary>
    Task<bool> CancelAsync(Guid tenantId, Guid fetchId, CancellationToken cancellationToken);
}

/// <summary>
/// The worker's half: pulling one file from somewhere else into storage.
///
/// <para>Separate from <see cref="IRemoteFetches"/> because nothing on a screen calls it and it
/// takes as long as a file takes. The hosted service is its only caller in production; the tests
/// call it directly, which is why the loop and the work are different types.</para>
/// </summary>
public interface IRemoteFetcher
{
    /// <summary>
    /// Pulls at most <paramref name="budget"/> queued fetches and reports how many it finished.
    /// Zero means there was nothing to do.
    /// </summary>
    Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken);
}
