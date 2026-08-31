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
    /// <param name="custody">
    /// How the finished file should be locked, or null to store it as it comes.
    ///
    /// <para><b>The customer's passphrase does not appear in this signature, and that is the whole
    /// change.</b> It used to: this method took what they typed, derived a wrapping key from it and
    /// wrapped a fresh content key. That was defensible on its own terms — the server is fetching
    /// the plaintext, so it holds the file either way — and it did not stay defensible, because
    /// people use one secret for everything. A server that had seen it once could open every file
    /// that customer had ever locked <i>in their own browser</i>, which is this product's central
    /// promise with an exception in it.</para>
    ///
    /// <para>The browser derives now and sends the wrapping. The customer still chooses the secret,
    /// and still chooses whether it is a passphrase or a recovery key — the server simply never
    /// learns which, or what.</para>
    /// </param>
    /// <param name="contentKey">
    /// The raw key for this one file, held in memory by <c>ContentKeyring</c> for the life of the
    /// job and written nowhere. Null exactly when <paramref name="custody"/> is.
    ///
    /// <para>It is still something the server holds, and that is unavoidable: it is about to encrypt
    /// a file it is downloading. What it no longer holds is the thing that opens everything else.
    /// <b>This is not what the browser's encryption promises</b> and must not be presented as though
    /// it were — see <c>SealedBy.Server</c>.</para>
    /// </param>
    Task<RemoteFetchStartResult> StartAsync(
        Guid tenantId,
        Guid? ownerUserId,
        string url,
        FetchCustody? custody,
        byte[]? contentKey,
        CancellationToken cancellationToken);

    /// <summary>This workspace's fetches, newest first.</summary>
    Task<IReadOnlyList<RemoteFetchView>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Stops one. What has already been written stays written.</summary>
    Task<bool> CancelAsync(Guid tenantId, Guid fetchId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a finished row off the list, and false for one that has not finished.
    ///
    /// <para>The upload screen is a work queue rather than a log, and a failure somebody has already
    /// read is not work. There is nothing lost by removing the row: it <i>is</i> the job, and a
    /// completed one's file is in the catalogue where anything that matters about it lives.</para>
    ///
    /// <para>Queued and running are refused rather than stopped-and-removed. Hiding a job that is
    /// still happening is worse than leaving it on screen, and the customer who wants it gone can
    /// press Cancel — which is the same one press, and says what it did.</para>
    /// </summary>
    Task<bool> DismissAsync(Guid tenantId, Guid fetchId, CancellationToken cancellationToken);

    /// <summary>Every finished row at once, returning how many went. Live work is left alone.</summary>
    Task<int> DismissFinishedAsync(Guid tenantId, CancellationToken cancellationToken);
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
