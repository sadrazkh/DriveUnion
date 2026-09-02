using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Can this deployment still get an access token for at least one account in the operator's pool?
///
/// <para>It is the one dependency that is neither the database nor this process, and the only one
/// whose failure is silent: a refresh token revoked in a Google console, or a Data Protection key
/// ring lost on a redeploy, leaves a panel that boots, draws every screen and cannot read or write a
/// single byte. Every other check here would stay green through all of it.</para>
///
/// <para><b>Why the answer is cached.</b> A readiness probe is polled every few seconds. Asking
/// Google every time would spend an OAuth request per poll — tens of thousands a day for a fact that
/// changes about twice a year — and would put this product's token endpoint quota in the hands of
/// whatever interval the platform happens to use. Sixty seconds is the interval, and it is chosen
/// from both ends: long enough that the cost is one refresh a minute in the worst case, short enough
/// that a pool reconnected by the operator is believed again while they are still on the screen.
/// Between polls the answer is a memory, and that is the point.</para>
///
/// <para><b>Single-flight, for the reason <see cref="GoogleTokenService"/> has one.</b> A burst of
/// probes arriving together must produce one attempt, not one each. The double-check after the gate
/// is what makes the other callers find the answer the first one just wrote.</para>
///
/// <para><b>An empty pool is ready.</b> A deployment that has never had an account connected is
/// exactly the deployment whose operator needs to reach the screen that connects one — and a
/// readiness probe that refuses is a container the platform never routes traffic to, so the panel
/// can never be set up at all. «No accounts» is not a fault; «accounts that cannot be used» is.</para>
///
/// <para><b>What it may leak: nothing.</b> Not the account's address, not Google's error text, not
/// which of the accounts was tried. The exception this swallows routinely contains a pool address —
/// <c>GoogleTokenService</c> names the account in the sentence it throws when a refresh token will
/// not decrypt — and that address is the one fact this product has always refused to show a
/// customer. It is dropped here rather than shortened.</para>
/// </summary>
internal sealed class GooglePoolTokenHealthCheck(
    IServiceScopeFactory scopes,
    TimeProvider clock) : IHealthCheck
{
    /// <summary>See the class remarks. Sixty seconds, from both ends of the argument.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the whole attempt gets.
    ///
    /// <para>Five seconds. The HTTP client behind a refresh has the framework's hundred-second
    /// timeout, which is right for an upload and absurd for a probe — a Google endpoint that has
    /// stopped answering would hold a readiness request open past any orchestrator's own deadline,
    /// and the platform would read that as a hung process rather than an unavailable dependency.
    /// A pool that cannot answer in five seconds is a pool no upload would survive either.</para>
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many accounts are tried before giving up.
    ///
    /// <para>Three. The question is «at least one», so the loop stops at the first success; this
    /// bounds the failure case, where every attempt is a round trip to Google. The pool is two
    /// accounts today and this is not the place to discover that it has become fifty.</para>
    /// </summary>
    private const int AccountsTried = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _answeredAt = DateTimeOffset.MinValue;

    private bool _answer;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await IsUsableAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy();
    }

    private async Task<bool> IsUsableAsync(CancellationToken cancellationToken)
    {
        if (Fresh(out var cached)) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(out cached)) return cached;

            var answer = await AskAsync(cancellationToken).ConfigureAwait(false);

            _answer = answer;

            // Stamped after the attempt rather than before it, so a slow attempt does not shorten
            // the interval the next caller gets.
            _answeredAt = clock.GetUtcNow();

            return answer;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Fresh(out bool answer)
    {
        answer = _answer;

        return clock.GetUtcNow() - _answeredAt < CacheFor;
    }

    private async Task<bool> AskAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<IGoogleTokenService>();

            // Healthy only. Disconnected is an account the operator already knows is broken, and
            // Paused is one they withheld on purpose — neither says anything about whether the pool
            // works, and both would drag a good deployment red.
            var accounts = await db.GoogleAccounts
                .AsNoTracking()
                .Where(account => account.Status == GoogleAccountStatus.Healthy)
                .OrderBy(account => account.Id)
                .Select(account => account.Id)
                .Take(AccountsTried)
                .ToListAsync(deadline.Token)
                .ConfigureAwait(false);

            if (accounts.Count == 0) return true;

            foreach (var accountId in accounts)
            {
                try
                {
                    // Usually free. The token service hands back the stored access token whenever it
                    // has more than a minute left on it, so this reaches Google roughly once an hour
                    // per account rather than once per probe.
                    await tokens.GetAccessTokenAsync(accountId, deadline.Token).ConfigureAwait(false);

                    return true;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The next account in the pool is the answer to this one being unusable. What
                    // the failure was belongs in the operator's accounts screen, which records it
                    // properly; it must not travel out of this method.
                }
            }

            return false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A database that has gone away lands here too. That is already reported by the
            // database check, and saying it twice is better than a probe that throws — an
            // unhandled exception inside a health check is a 500, and a 500 on /readyz is
            // indistinguishable to a platform from a panel that has stopped answering at all.
            return false;
        }
    }
}
