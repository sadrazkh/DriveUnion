using System.Net;
using System.Security.Cryptography;
using System.Text;
using DriveUnion.Web.Hosting;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Security;

/// <summary>
/// Turns a visitor's address into the value stored on <c>DownloadEvent.IpHash</c>.
/// </summary>
public interface IDownloadIpHasher
{
    string Hash(IPAddress? address);
}

/// <summary>
/// HMAC-SHA256, keyed.
///
/// A plain hash of an address is not an anonymisation: the whole IPv4 space is four billion
/// preimages, which is seconds of work. The key is what makes the stored value useless to anyone
/// who reads the table, while still letting the owner see that four hundred downloads came from one
/// party.
/// </summary>
internal sealed class DownloadIpHasher : IDownloadIpHasher
{
    private readonly byte[] key;

    public DownloadIpHasher(IOptions<DriveUnionWebOptions> options, ILogger<DownloadIpHasher> logger)
    {
        var configured = options.Value.DownloadIpHashKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // A random per-process key still protects the visitor; it only costs the owner the
            // ability to correlate a party across a restart. Refusing to start would take the
            // download path down over an analytics detail, which is the worse trade.
            key = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning(
                "DriveUnion:DownloadIpHashKey is not configured. Download events will use a key "
                + "generated for this process, so repeat visitors cannot be recognised across a restart.");
        }
        else
        {
            key = Encoding.UTF8.GetBytes(configured);
        }
    }

    public string Hash(IPAddress? address)
    {
        // An IPv4 client behind a dual-stack socket arrives as ::ffff:203.0.113.4 on some hops and
        // 203.0.113.4 on others. Normalising keeps one party from counting as two.
        var normalised = address is null
            ? "unknown"
            : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalised)));
    }
}
