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
/// <param name="FolderText">
/// Where the file was found, drawn only while a search is on. A result list mixes folders, and a row
/// that does not say where it came from is a row the reader has to go and look for afterwards.
/// Null while browsing, because there the answer is the breadcrumb above the table.
/// </param>
/// <summary>A label, and how many live files carry it. The count is zero on a row's own tags.</summary>
public sealed record TagViewModel(Guid Id, string Name, int FileCount);

public sealed record FileRowViewModel(
    Guid Id,
    string Name,
    string SizeText,
    string ModifiedText,
    int ActiveLinkCount,
    bool IsSelected,
    string? FolderText = null,
    IReadOnlyList<TagViewModel>? Tags = null)
{
    public IReadOnlyList<TagViewModel> Labels => Tags ?? [];

    /// <summary>«—» is a dash and not a word, so it has no language and stays a literal.</summary>
    public string LinkText => ActiveLinkCount == 0
        ? "—"
        : UiText.Files.LinkCount(ActiveLinkCount);
}

/// <summary>
/// One folder as a row above the files, in the same table and on the same tracks.
///
/// <para>A row rather than a second list, because a folder and a file are two things the reader is
/// choosing between and putting them in separate boxes makes that a choice about boxes. Size and
/// links are «—»: a folder has neither, and inventing a total would be inventing a number.</para>
/// </summary>
public sealed record FolderRowViewModel(Guid Id, string Name, int FileCount, int SubfolderCount)
{
    public string ContentsText => UiText.Files.FolderContents(FileCount, SubfolderCount);
}

/// <summary>One step of the breadcrumb. <paramref name="Id"/> null is the workspace's root.</summary>
public sealed record CrumbViewModel(Guid? Id, string Name, bool IsCurrent);

/// <summary>A «move to…» option: the full path, and the depth so the list can be indented.</summary>
public sealed record FolderChoiceViewModel(Guid? Id, string Label);

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

/// <param name="Query">
/// What the reader searched for, trimmed, or null when they are browsing.
///
/// <para>Carried on the page rather than left in the address bar: the header's box has to be
/// re-filled with it or a search returns rows next to an empty box, the empty state has to say
/// «nothing matched this» instead of «upload your first file», and every row and every form on the
/// screen has to lead somewhere that still has it — otherwise opening a result is what ends the
/// search.</para>
/// </param>
/// <param name="Folder">The folder being browsed, or null for the root. Always null while searching.</param>
/// <param name="Crumbs">Root first, the current folder last. One entry — the root — when at the top.</param>
/// <param name="Choices">Every folder in the workspace as a «move to…» option, with the root first.</param>
public sealed record FilesPageViewModel(
    IReadOnlyList<FileRowViewModel> Rows,
    FileDetailViewModel? Selected,
    AntiforgeryTokenViewModel Antiforgery,
    string? Notice,
    string? Query,
    Guid? Folder = null,
    IReadOnlyList<FolderRowViewModel>? Folders = null,
    IReadOnlyList<CrumbViewModel>? Crumbs = null,
    IReadOnlyList<FolderChoiceViewModel>? Choices = null,
    IReadOnlyList<FolderChoiceViewModel>? FolderChoices = null,
    IReadOnlyList<TagViewModel>? Tags = null,
    Guid? Tag = null)
{
    public bool IsSearching => Query is { Length: > 0 };

    public IReadOnlyList<TagViewModel> Labels => Tags ?? [];

    /// <summary>
    /// Whether the table is showing the whole workspace rather than one folder. A search does it and
    /// a tag filter does it, and everything that is about «where you are standing» — the breadcrumb,
    /// the folder rows, the folder toolbar — is off in both.
    /// </summary>
    public bool IsFiltered => IsSearching || Tag is not null;

    public TagViewModel? ActiveTag => Tag is { } id ? Labels.FirstOrDefault(t => t.Id == id) : null;

    public IReadOnlyList<FolderRowViewModel> FolderRows => Folders ?? [];

    public IReadOnlyList<CrumbViewModel> Breadcrumb => Crumbs ?? [];

    /// <summary>Where a file may be filed: anywhere in the workspace.</summary>
    public IReadOnlyList<FolderChoiceViewModel> MoveTargets => Choices ?? [];

    /// <summary>
    /// Where the folder being browsed may be moved: anywhere except itself and everything under it.
    ///
    /// <para>A second list rather than the first one with the current folder filtered out. Filtering
    /// one entry leaves its children in the list, and a folder offered its own child as a
    /// destination is offering the one move that is always refused — the reader picks it, presses
    /// the button, and is told no by a screen that suggested it.</para>
    /// </summary>
    public IReadOnlyList<FolderChoiceViewModel> FolderMoveTargets => FolderChoices ?? [];

    /// <summary>
    /// Whether the table has anything at all in it. Both halves, because a folder holding only
    /// folders is not an empty screen and «آپلود اولین فایل» under it would be the wrong sentence.
    /// </summary>
    public bool IsEmpty => Rows.Count == 0 && FolderRows.Count == 0;
}

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
