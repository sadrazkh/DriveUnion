using DriveUnion.Core.Application;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// A <see cref="ShareLinkSummary"/> carrying the one column it does not expose.
///
/// The panel lists links newest first, and SQLite will not sort a <c>DateTimeOffset</c> in SQL, so
/// <c>CreatedAt</c> has to travel back with the row and be dropped once the ordering is done.
/// </summary>
internal sealed record LinkRow(
    Guid Id,
    string Slug,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    int DownloadCount,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public ShareLinkSummary ToSummary() =>
        new(Id, Slug, ExpiresAt, MaxDownloads, DownloadCount, IsActive);
}
