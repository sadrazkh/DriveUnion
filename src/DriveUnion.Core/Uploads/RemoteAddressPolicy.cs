using System.Net;
using System.Net.Sockets;

namespace DriveUnion.Core.Uploads;

/// <summary>
/// Which addresses this server may open a connection to on a customer's say-so.
///
/// <para><b>What this is defending against.</b> «Fetch this URL for me» turns the server into a
/// request-forging tool: the customer chooses the address and the server dials it from inside the
/// operator's network, with whatever the operator's network trusts. <c>http://169.254.169.254/</c>
/// is the cloud metadata service and hands out credentials to anything that asks;
/// <c>http://localhost:5432</c> is the database; <c>http://10.0.0.7/admin</c> is whatever else the
/// deployment happens to be sitting next to. None of those need a bug anywhere else to be
/// catastrophic — they only need this list to be wrong.</para>
///
/// <para><b>Why it is a list of the refused rather than the permitted.</b> The whole point of the
/// feature is fetching from the public internet, which cannot be enumerated. So this is the one
/// place in the product where the safe list is the deny list, and it has to be complete — which is
/// why every entry below names what it is and why the IPv6 half is as long as the IPv4 half.</para>
///
/// <para><b>Pure, and separate from anything that opens a socket.</b> Every rule here is a function
/// of an address, so the tests are a table rather than a network. The half that cannot be pure —
/// resolving a name and refusing to connect to what it resolves <i>to</i> — is the handler that uses
/// this, and it calls this at connect time precisely so no rebinding can happen in between.</para>
/// </summary>
public static class RemoteAddressPolicy
{
    /// <summary>Whether this server may dial this address for a customer.</summary>
    public static bool IsReachable(IPAddress? address)
    {
        if (address is null) return false;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),

            // Neither IPv4 nor IPv6. Nothing on the public internet answers on one, and a family
            // this code has never been thought about is not a family to start dialling.
            _ => false,
        };
    }

    private static bool IsPublicV4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        return b[0] switch
        {
            0 => false,                                     // 0.0.0.0/8 — "this network"
            10 => false,                                    // 10/8 — private
            127 => false,                                   // 127/8 — loopback
            >= 224 => false,                                // 224/4 multicast, 240/4 reserved, broadcast
            100 => b[1] is < 64 or > 127,                   // 100.64/10 — carrier NAT
            169 => b[1] != 254,                             // 169.254/16 — link-local AND cloud metadata
            172 => b[1] is < 16 or > 31,                    // 172.16/12 — private
            192 => b[1] switch
            {
                0 => b[2] is not (0 or 2),                  // 192.0.0/24 IETF, 192.0.2/24 TEST-NET-1
                88 => b[2] != 99,                           // 192.88.99/24 — 6to4 relay anycast
                168 => false,                               // 192.168/16 — private
                _ => true,
            },
            198 => b[1] switch
            {
                18 or 19 => false,                          // 198.18/15 — benchmarking
                51 => b[2] != 100,                          // 198.51.100/24 — TEST-NET-2
                _ => true,
            },
            203 => !(b[1] == 0 && b[2] == 113),             // 203.0.113/24 — TEST-NET-3
            _ => true,
        };
    }

    private static bool IsPublicV6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;    // ::1
        if (address.IsIPv6LinkLocal) return false;          // fe80::/10
        if (address.IsIPv6SiteLocal) return false;          // fec0::/10, deprecated but still refused
        if (address.IsIPv6Multicast) return false;          // ff00::/8

        var b = address.GetAddressBytes();

        // :: — the unspecified address, which on some stacks connects to loopback.
        if (b.All(x => x == 0)) return false;

        // ::ffff:0:0/96 — an IPv4 address wearing an IPv6 hat, and the classic way past a check that
        // only looks at the two families separately. ::ffff:127.0.0.1 is loopback and would sail
        // through every rule above it. Unwrapped and asked again as what it actually is.
        if (address.IsIPv4MappedToIPv6) return IsPublicV4(address.MapToIPv4());

        // 2002::/16 (6to4) and 2001::/32 (Teredo) also carry an IPv4 address inside them, and both
        // are refused outright rather than unwrapped: they are transition mechanisms nothing this
        // product fetches from uses, and a tunnel is one more decoding to get wrong.
        if (b[0] == 0x20 && b[1] == 0x02) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;

        // 2001:db8::/32 — documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;

        // fc00::/7 — unique local, IPv6's answer to 10/8. IsIPv6UniqueLocal exists on newer
        // runtimes; the bit test is spelled out so this does not depend on which one is underneath.
        if ((b[0] & 0xfe) == 0xfc) return false;

        return true;
    }
}
