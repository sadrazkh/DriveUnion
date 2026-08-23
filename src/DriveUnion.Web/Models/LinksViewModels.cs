using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;

namespace DriveUnion.Web.Models;

/// <summary>
/// What the «وضعیت» column says about a link.
///
/// The first four are <see cref="ShareLinkAvailability"/> restated for a reader: the public side
/// collapses all of them into one identical card so that a visitor cannot tell an expired slug from
/// an invented one, and the owner's panel is the place that distinction was kept for.
///
/// <see cref="NearCap"/> is the fifth and has no counterpart there, because it is not a reason to
/// refuse anything — it is the comp's amber warning that a link is about to stop working.
/// </summary>
public enum LinkStatus
{
    Active,
    NearCap,
    CapReached,
    Expired,
    Revoked,
}

public static class LinkStatuses
{
    /// <summary>
    /// Three quarters spent turns the row amber. The comp's own rows fix the threshold between
    /// them: ۷۶/۱۰۰ is drawn «نزدیک سقف» and ۲۴۱/۵۰۰ is drawn «فعال», so it sits above 48% and at
    /// or below 76%. The quota bar's 80% would call the comp's warning row healthy.
    /// </summary>
    public const double NearCapFraction = 0.75;

    /// <summary>
    /// Ordered the way <see cref="ShareLink.Evaluate"/> orders it — revoked, then expired, then the
    /// cap — so the panel and the public route never disagree about why a link is not serving.
    /// </summary>
    public static LinkStatus Classify(ShareLinkSummary link, DateTimeOffset now)
    {
        if (!link.IsActive) return LinkStatus.Revoked;
        if (link.ExpiresAt is { } expiry && now >= expiry) return LinkStatus.Expired;
        if (link.MaxDownloads is not { } cap) return LinkStatus.Active;

        if (link.DownloadCount >= cap) return LinkStatus.CapReached;

        return link.DownloadCount >= cap * NearCapFraction ? LinkStatus.NearCap : LinkStatus.Active;
    }
}

/// <summary>
/// One row of «لینک‌های اشتراک»: file · address · downloads · expiry · status.
///
/// <see cref="FileId"/> is not drawn — it is where the row goes. The controls for a link (copy,
/// revoke, open the public page) already live on the file's detail panel, so the row is a way in
/// rather than a second set of buttons to keep in step with the first.
///
/// Nothing here names a Google account, and nothing here can: the listing behind it carries a file
/// name and a slug and no account at all.
/// </summary>
public sealed record LinkRowViewModel(
    Guid FileId,
    string FileName,
    string SlugPath,
    string DownloadsText,
    string ExpiryText,
    LinkStatus Status)
{
    public string StatusText => Status switch
    {
        LinkStatus.Active => "فعال",
        LinkStatus.NearCap => "نزدیک سقف",
        LinkStatus.CapReached => "سقف تکمیل",
        LinkStatus.Expired => "منقضی",
        _ => "غیرفعال",
    };

    /// <summary>
    /// The comp colours this cell with a token rather than badging it, and there is no utility
    /// class for coloured cell text — so the mapping from state to colour is decided here, once,
    /// instead of in a chain of conditionals inside the view.
    /// </summary>
    public string StatusStyle => Status switch
    {
        LinkStatus.Active => "color: var(--accent-ink);",
        LinkStatus.NearCap => "color: var(--warn);",
        _ => "color: var(--muted);",
    };
}

public sealed record LinksPageViewModel(IReadOnlyList<LinkRowViewModel> Rows);
