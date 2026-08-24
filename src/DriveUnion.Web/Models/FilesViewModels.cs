using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One row of «فایل‌ها».
///
/// The comp has an «اکانت» column and the detail panel has «اکانت» and «شناسه درایو» rows. None of
/// the three exist here, and that is the product model rather than an omission: a customer must
/// never learn which Google account holds their file.
///
/// The comp's «دانلود» column is absent too, for a duller reason — <c>FileListItem</c> carries an
/// active link count and no download total, so the list says how many links a file has and the
/// detail panel says how often each was pulled.
/// </summary>
public sealed record FileRowViewModel(
    Guid Id,
    string Name,
    string SizeText,
    string ModifiedText,
    int ActiveLinkCount,
    bool IsSelected)
{
    /// <summary>«—» is a dash and not a word, so it has no language and stays a literal.</summary>
    public string LinkText => ActiveLinkCount == 0
        ? "—"
        : UiText.Files.LinkCount(ActiveLinkCount);
}

public sealed record ShareLinkViewModel(
    Guid Id,
    string Slug,
    string DisplayUrl,
    string PublicUrl,
    string DownloadsText,
    string ExpiryText,
    bool IsActive);

public sealed record FileDetailViewModel(
    Guid Id,
    string Name,
    string SizeText,
    string KindText,
    string CreatedText,
    IReadOnlyList<ShareLinkViewModel> Links)
{
    public ShareLinkViewModel? ActiveLink => Links.FirstOrDefault(link => link.IsActive);
}

public sealed record FilesPageViewModel(
    IReadOnlyList<FileRowViewModel> Rows,
    FileDetailViewModel? Selected,
    AntiforgeryTokenViewModel Antiforgery,
    string? Notice);

/// <summary>
/// Handed to the islands so a <c>fetch</c> can carry the token the write APIs demand. The panel is
/// cookie-authenticated, so every state-changing call needs one and this is the only place the
/// frontend can get it.
/// </summary>
public sealed record AntiforgeryTokenViewModel(string HeaderName, string Token);

public sealed record ErrorViewModel(string? RequestId)
{
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
