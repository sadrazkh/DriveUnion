using DriveUnion.Core.Sharing;

namespace DriveUnion.Core.Application;

public sealed record ShareLinkSummary(
    Guid Id,
    string Slug,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    int DownloadCount,
    bool IsActive);

public sealed record CreateShareLinkRequest(
    Guid StoredFileId,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads);

/// <summary>The owner's side of a link. Tenant-scoped, like everything else in the panel.</summary>
public interface IShareLinkService
{
    Task<ShareLinkSummary> CreateAsync(
        Guid tenantId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShareLinkSummary>> ListForFileAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken);
}

/// <summary>What the public landing page shows. No account, no file id, no tenant.</summary>
public sealed record PublicFileView(
    string Slug,
    string FileName,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    int DownloadCount,
    int? MaxDownloads,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Everything the streaming route needs. <see cref="GoogleAccountId"/> and
/// <see cref="DriveFileId"/> stay server-side — they must never reach a response body, header or URL.
/// </summary>
public sealed record PublicDownloadTicket(
    Guid ShareLinkId,
    Guid GoogleAccountId,
    string DriveFileId,
    string FileName,
    string MimeType,
    long SizeBytes);

/// <summary>
/// A slug lookup. <c>Reason</c> is null when no such slug exists, and set when a real link is
/// refusing — a distinction for the logs and the owner's panel only. The visitor gets one identical
/// card either way, because telling "expired" apart from "never existed" is enough to enumerate the
/// slug space.
/// </summary>
public sealed record PublicLinkResolution(
    bool IsAvailable,
    ShareLinkAvailability? Reason,
    PublicFileView? File)
{
    public static readonly PublicLinkResolution NotFound = new(false, null, null);
}

/// <summary>
/// The public path, and the reason this interface exists separately from <see cref="IFileCatalog"/>.
///
/// /d/{slug} is anonymous. It has no tenant and must not acquire one: a reader that took a tenantId
/// would be handed <c>Guid.Empty</c> by an anonymous request and would 404 every live link in the
/// product while the rows sat plainly in the table. So this type has no tenant concept at all, and
/// that absence is load-bearing.
/// </summary>
public interface IPublicLinkReader
{
    Task<PublicLinkResolution> ResolveAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Null when the slug is unknown or the link is refusing. The caller renders one card.</summary>
    Task<PublicDownloadTicket?> ResolveForDownloadAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Records one counted download. The caller decides whether a request counts — see
    /// <see cref="DownloadCounting"/> — because that decision is about the Range header, not the row.
    /// </summary>
    Task RecordDownloadAsync(
        Guid shareLinkId,
        string ipHash,
        string? userAgent,
        CancellationToken cancellationToken);
}
