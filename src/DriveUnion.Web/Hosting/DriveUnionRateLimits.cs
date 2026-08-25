namespace DriveUnion.Web.Hosting;

/// <summary>
/// Names of the limiter policies applied to <c>/d/*</c>.
///
/// Two policies rather than one because the two routes cost different things. The landing page is
/// cheap and its only real risk is somebody walking the slug space; the stream opens a connection to
/// Google and can run for hours, and a single video player legitimately issues dozens of ranged
/// requests while a viewer scrubs.
/// </summary>
public static class DriveUnionRateLimits
{
    public const string PublicPage = "DriveUnion.PublicPage";

    public const string PublicDownload = "DriveUnion.PublicDownload";

    /// <summary>
    /// <c>/api/v1/*</c>, partitioned per key rather than per address.
    ///
    /// <para>The two above are keyed on the caller's address, which is all an anonymous visitor has.
    /// An API caller has a key, and the key is what a limit should follow: two customers behind one
    /// office NAT must not spend each other's budget, and one customer moving between machines must
    /// not get a fresh budget by doing so.</para>
    /// </summary>
    public const string Api = "DriveUnion.Api";
}
