namespace DriveUnion.Core.Sharing;

/// <summary>
/// One counted download. The audit trail behind <see cref="ShareLink.DownloadCount"/>.
///
/// The visitor's address is stored hashed, never raw: the owner needs to see "this link was pulled
/// 400 times by one party", not who that party is.
/// </summary>
public sealed class DownloadEvent
{
    public Guid Id { get; set; }

    public Guid ShareLinkId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string IpHash { get; set; }

    public string? UserAgent { get; set; }
}
