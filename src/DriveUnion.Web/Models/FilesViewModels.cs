using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
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
    IReadOnlyList<TagViewModel>? Tags = null,
    bool IsEncrypted = false)
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

/// <summary>
/// A clean-up still running, as one line above the table.
///
/// <para>It carries the finished sentence rather than the job's scope, because the catalogue cannot
/// take an enum — a screen text that switched on one would be a string no test could render. The
/// switch is <see cref="FromJob"/>, here, where it is one expression.</para>
/// </summary>
public sealed record DeletionProgressViewModel(string Text)
{
    public static DeletionProgressViewModel FromJob(DeletionJobView job)
    {
        ArgumentNullException.ThrowIfNull(job);

        // A folder job with no name is not a shape the queue writes — but a name is nullable on the
        // row, and «the folder called nothing» is a worse sentence than the one without a folder in
        // it at all.
        return new DeletionProgressViewModel(
            job.Scope == DeletionScope.Folder && job.FolderName is { Length: > 0 } folder
                ? UiText.Files.TidyingFolder(folder, job.Done, job.Total)
                : UiText.Files.TidyingSelection(job.Done, job.Total));
    }
}

/// <summary>A «move to…» option: the full path, and the depth so the list can be indented.</summary>
public sealed record FolderChoiceViewModel(Guid? Id, string Label);

public sealed record ShareLinkViewModel(
    Guid Id,
    string Slug,
    string DisplayUrl,
    string PublicUrl,
    string DownloadsText,
    string ExpiryText,
    bool IsActive,

    /// <summary>
    /// Whether this link carries its own wrapped key, for a locked file.
    ///
    /// <para>The panel has to tell the two apart. A link with one is opened by a secret made for it;
    /// a link without one is opened by the owner's own passphrase — which also opens everything
    /// uploaded in the same batch. Those are very different things to have given somebody, and a
    /// screen that showed them identically would leave the owner no way to know which they had
    /// done.</para>
    /// </summary>
    bool HasOwnKey = false);

public sealed record FileDetailViewModel(
    Guid Id,
    string Name,
    string SizeText,
    string KindText,
    string CreatedText,
    IReadOnlyList<ShareLinkViewModel> Links,
    bool IsEncrypted = false,

    /// <summary>
    /// The length in bytes, beside <see cref="SizeText"/> rather than instead of it.
    ///
    /// <para>The text is for reading and this is for arithmetic: locking a file seals it into
    /// segments, and the browser has to know how many there will be before it has seen a byte. A
    /// number parsed back out of «۱۸٫۴ مگابایت» would be a different number.</para>
    /// </summary>
    long SizeBytes = 0,

    /// <summary>
    /// «video», «audio», or empty for a file no browser can play.
    ///
    /// <para>Decided by <c>Previews</c> and not by the view, so the question «is this type safe to
    /// hand to a media element» has one answer in this product rather than one per screen. Unlike
    /// the public card's version it ignores encryption: the panel can play a locked file, because
    /// the owner has the passphrase and the service worker does the decrypting.</para>
    /// </summary>
    string MediaKind = "",

    /// <summary>
    /// Which of the two kinds of encryption this file got, as a sentence rather than a word.
    ///
    /// <para>Null when it is not encrypted at all. The two are the same format and very different
    /// promises, and the difference is invisible from the outside — so the panel spells it out
    /// beside the padlock, which is the place somebody looks to find out what they have.</para>
    /// </summary>
    string? SealedByText = null,

    /// <summary>
    /// The file's header as JSON, for the island that shares it — and null for a file that is not
    /// locked.
    ///
    /// <para>It is the owner's own file in their own authenticated panel, and it is the same header
    /// <c>/d/{slug}</c> already hands to anyone holding a link: none of it opens anything without
    /// the secret it is wrapped with. It is here because sharing a locked file means opening it in
    /// this browser and re-wrapping its key, and that cannot happen anywhere else.</para>
    /// </summary>
    string? EncryptionJson = null)
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
    Guid? Tag = null,

    /// <summary>
    /// The clean-ups this workspace still has running, which is nearly always none.
    ///
    /// <para>Drawn on this screen because this is the screen the delete was pressed on. It is not a
    /// progress bar the reader is waiting behind — their files are already in the trash — it is what
    /// stops «the trash is still filling up half a minute later» being something they have to
    /// notice.</para>
    /// </summary>
    IReadOnlyList<DeletionProgressViewModel>? Deletions = null,

    /// <summary>
    /// Files being locked, for the same reason the clean-ups above are here.
    ///
    /// <para>Stronger, in fact: a delete the customer pressed does what they expect and the row is
    /// only reassurance, whereas a lock is a file quietly becoming unreadable over the next few
    /// minutes. A customer who does not see this happening is one who opens the file, finds a
    /// passphrase prompt, and has to work out why.</para>
    /// </summary>
    IReadOnlyList<FileLockRowViewModel>? Locks = null)
{
    public bool IsSearching => Query is { Length: > 0 };

    public IReadOnlyList<DeletionProgressViewModel> Tidying => Deletions ?? [];

    public IReadOnlyList<FileLockRowViewModel> Locking => Locks ?? [];

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

/// <summary>One queued or finished «fetch this for me», as the upload screen draws it.</summary>
/// <summary>
/// A file being locked, on the screen it was asked for from.
///
/// <para>It is not a progress bar in front of something the reader is waiting for — they can close
/// the page. It is here so that a file which has quietly become unreadable in the next few minutes
/// is one the customer was told about, rather than one they discover by opening it.</para>
/// </summary>
public sealed record FileLockRowViewModel(
    Guid Id,
    string Name,
    string StatusText,
    bool IsLive,
    string ProgressText,
    string? FailureReason);

public sealed record RemoteFetchRowViewModel(
    Guid Id,
    string Url,
    string Name,
    string StatusText,
    bool IsLive,
    string ProgressText,
    string? FailureReason);

/// <summary>
/// The upload screen: the antiforgery pair the island needs, and the links the server is pulling.
/// </summary>
public sealed record UploadPageViewModel(
    AntiforgeryTokenViewModel Antiforgery,
    IReadOnlyList<RemoteFetchRowViewModel> Fetches,
    string? Notice,
    string? Error);
