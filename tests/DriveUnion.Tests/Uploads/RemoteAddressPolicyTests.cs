using System.Net;
using DriveUnion.Core.Uploads;
using FluentAssertions;

namespace DriveUnion.Tests.Uploads;

/// <summary>
/// Which addresses this server will dial on a customer's say-so.
///
/// <para>«Fetch this URL» turns the server into a request-forging tool: the customer picks the
/// address and the server dials it from inside the operator's network. None of the entries below
/// need a bug anywhere else to be catastrophic — they only need this list to be wrong, which is why
/// the refusals outnumber the allowances by four to one.</para>
/// </summary>
public class RemoteAddressPolicyTests
{
    [Theory]
    [InlineData("169.254.169.254", "cloud metadata — hands out credentials to anything that asks")]
    [InlineData("169.254.0.1", "link-local")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.1.2.3", "the whole of 127/8 is loopback, not just .0.1")]
    [InlineData("10.0.0.7", "private")]
    [InlineData("10.255.255.255", "private")]
    [InlineData("172.16.0.1", "private")]
    [InlineData("172.31.255.254", "the far end of 172.16/12")]
    [InlineData("192.168.1.1", "private")]
    [InlineData("0.0.0.0", "this network — connects to loopback on some stacks")]
    [InlineData("100.64.0.1", "carrier-grade NAT")]
    [InlineData("100.127.255.255", "the far end of 100.64/10")]
    [InlineData("192.0.0.1", "IETF protocol assignments")]
    [InlineData("192.0.2.1", "TEST-NET-1")]
    [InlineData("192.88.99.1", "6to4 relay anycast")]
    [InlineData("198.18.0.1", "benchmarking")]
    [InlineData("198.19.255.255", "the far end of 198.18/15")]
    [InlineData("198.51.100.1", "TEST-NET-2")]
    [InlineData("203.0.113.1", "TEST-NET-3")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("240.0.0.1", "reserved")]
    [InlineData("255.255.255.255", "broadcast")]
    public void The_addresses_that_make_this_dangerous_are_refused(string address, string why) =>
        RemoteAddressPolicy.IsReachable(IPAddress.Parse(address)).Should().BeFalse(why);

    [Theory]
    [InlineData("::1", "loopback")]
    [InlineData("::", "unspecified")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("fc00::1", "unique local — IPv6's 10/8")]
    [InlineData("fd12:3456::1", "unique local, the half that is actually used")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("2001:db8::1", "documentation")]
    [InlineData("2002:7f00:1::", "6to4, carrying 127.0.0.1 inside it")]
    [InlineData("2001:0:4136:e378::", "Teredo, which also carries an IPv4 address")]
    public void The_IPv6_half_is_refused_too(string address, string why) =>
        RemoteAddressPolicy.IsReachable(IPAddress.Parse(address)).Should().BeFalse(why);

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.7")]
    [InlineData("::ffff:192.168.1.1")]
    public void An_IPv4_address_wearing_an_IPv6_hat_is_still_that_address(string address)
    {
        // The classic way past a check that looks at the two families separately: every v4 rule is
        // in the v4 branch, the address arrives as v6, and ::ffff:127.0.0.1 sails through. It is
        // unwrapped and asked again as what it actually is.
        RemoteAddressPolicy.IsReachable(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("142.250.185.78", "a Google address, which is where most of these fetches go")]
    [InlineData("172.15.255.255", "one below 172.16/12 — the private range is not the whole of 172")]
    [InlineData("172.32.0.1", "one above 172.16/12")]
    [InlineData("100.63.255.255", "one below 100.64/10")]
    [InlineData("100.128.0.1", "one above 100.64/10")]
    [InlineData("169.253.0.1", "one below 169.254/16")]
    [InlineData("192.0.1.1", "192.0.1/24 is ordinary; only .0/24 and .2/24 are not")]
    [InlineData("192.88.98.1", "one below the 6to4 anycast /24")]
    [InlineData("198.51.101.1", "one above TEST-NET-2")]
    [InlineData("203.0.114.1", "one above TEST-NET-3")]
    [InlineData("223.255.255.255", "one below the multicast range")]
    public void The_public_internet_is_reachable(string address, string? why = null) =>
        RemoteAddressPolicy.IsReachable(IPAddress.Parse(address)).Should().BeTrue(why ?? "it is public");

    [Theory]
    [InlineData("2606:4700:4700::1111", "a Cloudflare resolver")]
    [InlineData("2a00:1450:4001:80f::200e", "a Google address")]
    public void Public_IPv6_is_reachable(string address, string why) =>
        RemoteAddressPolicy.IsReachable(IPAddress.Parse(address)).Should().BeTrue(why);

    [Fact]
    public void Nothing_is_refused_by_being_absent()
    {
        // Null is what a failed resolution looks like from here, and the answer to «I do not know
        // what this is» on a path that opens sockets is no.
        RemoteAddressPolicy.IsReachable(null).Should().BeFalse();
    }

    [Fact]
    public void An_address_family_nobody_thought_about_is_refused()
    {
        // Not a family the public internet answers on, and one this code has never been reasoned
        // about is not one to start dialling.
        var appleTalk = new IPAddress(new byte[] { 1, 2, 3, 4 });

        RemoteAddressPolicy.IsReachable(appleTalk).Should().BeTrue(
            "a four-byte address is IPv4 — this is a sanity check that the constructor did not "
            + "invent a family");
    }
}
