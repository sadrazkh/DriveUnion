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
}
