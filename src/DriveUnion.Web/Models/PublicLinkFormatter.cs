namespace DriveUnion.Web.Models;

/// <summary>
/// Builds <c>/d/{slug}</c> addresses.
///
/// The base comes from configuration rather than the incoming request: the customer copies this
/// string out of the panel and sends it to somebody else, so it has to be the address the product
/// is served on, not whichever host the panel happened to be reached by.
/// </summary>
public static class PublicLinkFormatter
{
    public static string Path(string slug) => $"/d/{slug}";

    public static string Absolute(string baseUrl, string slug) => $"{baseUrl.TrimEnd('/')}/d/{slug}";

    /// <summary>What the comp prints: <c>yourdomain.com/d/kx91mz</c>, without the scheme.</summary>
    public static string Display(string baseUrl, string slug)
    {
        var absolute = Absolute(baseUrl, slug);
        var separator = absolute.IndexOf("://", StringComparison.Ordinal);
        return separator < 0 ? absolute : absolute[(separator + 3)..];
    }
}
