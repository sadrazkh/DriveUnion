namespace DriveUnion.Core.Sharing;

/// <summary>
/// Why a link cannot be served.
///
/// The public page collapses every one of these — plus an unknown slug — into a single identical
/// card. The distinction exists for the owner's panel and for logs, never for the visitor: telling
/// "expired" apart from "never existed" is enough to enumerate the slug space.
/// </summary>
public enum ShareLinkAvailability
{
    Available = 0,
    Revoked = 1,
    Expired = 2,
    DownloadCapReached = 3,
}

public sealed class ShareLink
{
    public Guid Id { get; set; }

    /// <summary>The public identifier in <c>/d/{slug}</c>. Unique across the whole product.</summary>
    public required string Slug { get; set; }

    public Guid StoredFileId { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public int? MaxDownloads { get; set; }

    /// <summary>Denormalised from <see cref="DownloadEvent"/> because the panel reads it constantly.</summary>
    public int DownloadCount { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public ShareLinkAvailability Evaluate(DateTimeOffset now)
    {
        if (!IsActive) return ShareLinkAvailability.Revoked;
        if (ExpiresAt is { } expiry && now >= expiry) return ShareLinkAvailability.Expired;
        if (MaxDownloads is { } cap && DownloadCount >= cap) return ShareLinkAvailability.DownloadCapReached;
        return ShareLinkAvailability.Available;
    }
}
