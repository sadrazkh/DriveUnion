using DriveUnion.Core.Api;
using DriveUnion.Core.Application;

namespace DriveUnion.Web.Models.Api;

/// <summary>
/// The shapes <c>/api/v1</c> answers with.
///
/// <para><b>Separate from <c>Models/</c> on purpose.</b> Those records serve views and the panel's
/// own islands, and they change whenever a screen does — a page that stops drawing a column drops
/// it. These are a contract somebody writes a program against: a field removed here breaks a
/// customer's script at three in the morning, so they are their own file, under a version, and they
/// exist to be dull.</para>
///
/// <para><b>Nothing here names a Google account, a Drive id or a folder in the operator's pool.</b>
/// That is the product's rule everywhere and it binds hardest here — a JSON field is forever in a
/// way a rendered table is not.</para>
///
/// <para>Times are <c>DateTimeOffset</c> and serialise as ISO-8601 with an offset, so a caller in
/// any zone reads the same instant. Sizes are bytes, as integers, never formatted.</para>
/// </summary>
public sealed record V1File(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    Guid? FolderId,
    int ActiveLinkCount,
    IReadOnlyList<string> Labels)
{
    public static V1File From(FileListItem file, IReadOnlyList<TagSummary> labels)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(labels);

        return new V1File(
            file.Id,
            file.Name,
            file.MimeType,
            file.SizeBytes,
            file.ModifiedAt,
            file.FolderId,
            file.ActiveLinkCount,
            [.. labels.Select(l => l.Name)]);
    }
}

/// <summary>
/// A wrapper rather than a bare array.
///
/// <para>A top-level JSON array is a shape that cannot grow: the day this needs a cursor or a total
/// there is nowhere to put one without breaking every caller. An object with one property costs a
/// reader four characters and keeps that door open.</para>
/// </summary>
public sealed record V1FileListResponse(IReadOnlyList<V1File> Files);

public sealed record V1FileDetail(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<V1FileLink> Links)
{
    public static V1FileDetail From(FileDetail file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new V1FileDetail(
            file.Id,
            file.Name,
            file.MimeType,
            file.SizeBytes,
            file.CreatedAt,
            file.ModifiedAt,
            [.. file.Links.Select(l => new V1FileLink(l.Id, l.Slug, l.IsActive, l.DownloadCount, l.MaxDownloads, l.ExpiresAt))]);
    }
}

public sealed record V1FileLink(
    Guid Id,
    string Slug,
    bool IsActive,
    int DownloadCount,
    int? MaxDownloads,
    DateTimeOffset? ExpiresAt);

/// <param name="Url">The whole public address, so a caller never has to know how one is assembled.</param>
public sealed record V1Link(Guid Id, string Slug, string Url, DateTimeOffset? ExpiresAt, int? MaxDownloads)
{
    public static V1Link From(ShareLinkSummary link, string publicBase)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new V1Link(
            link.Id,
            link.Slug,
            PublicLinkFormatter.Absolute(publicBase, link.Slug),
            link.ExpiresAt,
            link.MaxDownloads);
    }
}

public sealed record V1CreateLinkRequest(DateTimeOffset? ExpiresAt, int? MaxDownloads);

public sealed record V1MoveRequest(Guid? FolderId);

public sealed record V1Folder(Guid Id, string Name, int FileCount, int SubfolderCount);

public sealed record V1FolderListResponse(IReadOnlyList<V1Folder> Folders);

public sealed record V1CreateFolderRequest(Guid? ParentId, string? Name);

/// <summary>
/// What the workspace has spent and what it may spend.
///
/// <para>The figure P9 made real. It is here because a program that uploads on a schedule is the
/// thing most likely to walk into a ceiling, and «you are over» arriving as a refused request with
/// nothing to check beforehand is the worst version of that.</para>
/// </summary>
public sealed record V1Usage(
    long StorageUsedBytes,
    long StorageLimitBytes,
    long MaxFileBytes,
    long TrafficThisMonthBytes,
    long TrafficLimitBytes,
    int DownloadsThisMonth);

/// <summary>A key as the API reports it. The secret is not here and cannot be.</summary>
public sealed record V1ApiKey(
    Guid Id,
    string Name,
    string Prefix,
    ApiScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public static V1ApiKey From(ApiTokenSummary token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return new V1ApiKey(
            token.Id,
            token.Name,
            token.Prefix,
            token.Scope,
            token.CreatedAt,
            token.LastUsedAt,
            token.ExpiresAt,
            token.RevokedAt);
    }
}
