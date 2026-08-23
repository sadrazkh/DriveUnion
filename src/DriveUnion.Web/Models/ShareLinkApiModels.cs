using System.ComponentModel.DataAnnotations;
using DriveUnion.Core.Application;

namespace DriveUnion.Web.Models;

public sealed class CreateShareLinkPayload
{
    public Guid StoredFileId { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    [Range(1, int.MaxValue)]
    public int? MaxDownloads { get; init; }
}

public sealed record ShareLinkResponse(
    Guid Id,
    string Slug,
    string Url,
    string DisplayUrl,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    int DownloadCount,
    bool IsActive)
{
    public static ShareLinkResponse From(ShareLinkSummary summary, string publicBaseUrl) => new(
        summary.Id,
        summary.Slug,
        PublicLinkFormatter.Absolute(publicBaseUrl, summary.Slug),
        PublicLinkFormatter.Display(publicBaseUrl, summary.Slug),
        summary.ExpiresAt,
        summary.MaxDownloads,
        summary.DownloadCount,
        summary.IsActive);
}

/// <summary>What the panel's file list needs. No account, no Drive id — see <see cref="IFileCatalog"/>.</summary>
public sealed record FileResponse(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    string SizeText,
    DateTimeOffset ModifiedAt,
    int ActiveLinkCount)
{
    public static FileResponse From(FileListItem item) => new(
        item.Id,
        item.Name,
        item.MimeType,
        item.SizeBytes,
        DisplayFormats.Bytes(item.SizeBytes),
        item.ModifiedAt,
        item.ActiveLinkCount);
}

public sealed record FileDetailResponse(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    string SizeText,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<ShareLinkResponse> Links)
{
    public static FileDetailResponse From(FileDetail detail, string publicBaseUrl) => new(
        detail.Id,
        detail.Name,
        detail.MimeType,
        detail.SizeBytes,
        DisplayFormats.Bytes(detail.SizeBytes),
        detail.CreatedAt,
        detail.ModifiedAt,
        [.. detail.Links.Select(link => ShareLinkResponse.From(link, publicBaseUrl))]);
}
