using System.Net;
using System.Net.Sockets;
using DriveUnion.Core.Uploads;

namespace DriveUnion.Infrastructure.Uploads;

/// <summary>
/// Thrown when the address a URL resolves to is one this server will not dial.
///
/// <para>Its own type rather than a generic socket failure, because the caller has to tell «that
/// host is on the refused list» apart from «that host is down» — they are different sentences to a
/// customer and only one of them is worth retrying.</para>
/// </summary>
public sealed class RemoteAddressRefusedException(string message) : Exception(message);

/// <summary>
/// The HTTP stack a customer-supplied URL is fetched through, and the reason DNS rebinding does not
/// work against it.
///
/// <para><b>The window this closes.</b> The obvious design resolves the host, checks the addresses
/// against <see cref="RemoteAddressPolicy"/>, and then hands the URL to <c>HttpClient</c> — which
/// resolves it again. Between those two resolutions an attacker who controls the name answers once
/// with a public address and once with <c>169.254.169.254</c>, and every check passes while the
/// socket goes somewhere else entirely. It is not theoretical; it is the standard bypass.</para>
///
/// <para><b>What happens instead.</b> The resolution happens <i>inside</i> the connect callback, and
/// the socket is opened to one of the addresses that resolution returned. There is no second
/// lookup, so there is no window: what was checked is what is dialled.</para>
///
/// <para>Every redirect goes through this callback too, because each hop opens its own connection —
/// so a public URL that 302s to the metadata service is refused at the second hop.</para>
/// </summary>
public static class GuardedFetchHandler
{
    /// <summary>
    /// How many redirects are followed.
    ///
    /// <para>Five. Every hop is checked, so this is not a safety limit — it is a limit on how long a
    /// server that enjoys redirecting can hold a worker.</para>
    /// </summary>
    public const int MaxRedirects = 5;

    /// <summary>How long a connection may take to open. A host that is not answering is not a fetch.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = MaxRedirects,

        // Nothing is sent that a redirect could carry somewhere it should not go. The runtime
        // already drops Authorization across hosts; this fetches with no credentials at all, so
        // there is nothing to drop.
        UseCookies = false,
        Credentials = null,
        UseProxy = false,

        // A pool that outlives the request would hold connections to hosts a later check might
        // refuse. These are one-shot fetches of somebody else's server.
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),

        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        // An address literal in the URL — http://169.254.169.254/ — arrives here as a host that is
        // already an address. Dns.GetHostAddressesAsync returns it unchanged, so it is checked by
        // the same loop rather than by a special case somebody could forget.
        var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);

        var reachable = addresses.Where(RemoteAddressPolicy.IsReachable).ToArray();

        if (reachable.Length == 0)
        {
            // Every address this name has is refused — or it has none. One sentence for both,
            // because the customer's next action is the same and the difference is not theirs.
            throw new RemoteAddressRefusedException(
                $"{host} resolves to no address this server is allowed to open a connection to.");
        }

        // Tried in order, because a name with several addresses may have one that is simply down.
        // Every one of them has already passed the policy — this loop cannot reach an address that
        // did not, which is the property the whole class exists for.
        Exception? last = null;

        foreach (var address in reachable)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token)
                    .ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception)
            {
                socket.Dispose();
                last = exception;

                // A cancelled connect is the caller giving up or the timeout firing, and trying the
                // next address would ignore both.
                if (timeout.IsCancellationRequested) throw;
            }
        }

        throw last ?? new IOException($"Could not open a connection to {host}.");
    }
}
