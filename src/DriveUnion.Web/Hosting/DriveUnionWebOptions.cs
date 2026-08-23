namespace DriveUnion.Web.Hosting;

/// <summary>
/// The <c>DriveUnion</c> configuration section, as the HTTP surface needs it.
/// </summary>
public sealed class DriveUnionWebOptions
{
    public const string SectionName = "DriveUnion";

    /// <summary>
    /// Origin of the public link, e.g. <c>https://example.com</c>. The panel prints
    /// <c>{PublicBaseUrl}/d/{slug}</c> for the customer to copy, so it has to be the address the
    /// visitor will use rather than whatever host the panel request happened to arrive on.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Key for the HMAC over a visitor's address. See <c>DownloadIpHasher</c> — an unkeyed hash of
    /// an IPv4 address is not anonymisation.
    /// </summary>
    public string? DownloadIpHashKey { get; set; }
}
