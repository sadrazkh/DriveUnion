using DriveUnion.Core.Application;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One file waiting in the trash, as its row is drawn.
///
/// <para>Nothing here names the Google account holding the bytes, and nothing can: <see cref="TrashItem"/>
/// carries a name, a size and two moments. That is the same rule the files table follows, restated
/// where a second screen could otherwise have widened it.</para>
/// </summary>
/// <param name="RestoreLabel">
/// The accessible name of this row's button. Every row's control says the same word, so without the
/// file name a screen reader is read a column of identical «بازگردانی»s.
/// </param>
public sealed record TrashRowViewModel(
    Guid Id,
    string Name,
    string SizeText,
    string DeletedText,
    string PurgeText,
    string RestoreLabel)
{
    public static TrashRowViewModel From(TrashItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new TrashRowViewModel(
            item.Id,
            item.Name,
            DisplayFormats.Bytes(item.SizeBytes),
            DisplayFormats.Relative(item.DeletedAt, now),
            PurgeCell(item.PurgeAfter, now),
            UiText.Trash.RestoreNamed(item.Name));
    }

    /// <summary>
    /// When the sweeper may take it.
    ///
    /// <para>A null deadline is a file deleted before the trash existed. The purge leaves those alone
    /// rather than inventing a deadline for something somebody deleted under other rules, so the cell
    /// says so — a blank would be read as «never», which is the opposite of what an empty trash
    /// button will do to it.</para>
    /// </summary>
    private static string PurgeCell(DateTimeOffset? purgeAfter, DateTimeOffset now) =>
        DisplayFormats.DaysUntil(purgeAfter, now) switch
        {
            null => UiText.Trash.PurgeNoDeadline,

            // Past its deadline, or inside the last day of it. The sweeper runs on its own schedule
            // and against a shared request budget, so promising a time here would be a promise this
            // screen cannot keep.
            0 => UiText.Trash.PurgeDue,

            { } days => UiText.Trash.PurgeInDays(days),
        };
}

/// <summary>
/// «سطل زباله» as the customer's own screen renders it.
///
/// <para>The total is on the page rather than only on the sidebar card because it is the answer to
/// the question that brought them here: it is exactly the space they believe they freed and have
/// not. The button beside it is the only thing in the product that gives it back on demand.</para>
/// </summary>
public sealed class TrashPageViewModel
{
    public TrashPageViewModel(IReadOnlyList<TrashItem> items, DateTimeOffset now, string? notice)
    {
        ArgumentNullException.ThrowIfNull(items);

        Rows = [.. items.Select(item => TrashRowViewModel.From(item, now))];
        CountText = UiText.Trash.FileCount(items.Count);
        HoldingText = UiText.Trash.HoldingSize(DisplayFormats.Bytes(items.Sum(item => item.SizeBytes)));
        Notice = notice;
    }

    public IReadOnlyList<TrashRowViewModel> Rows { get; }

    public string CountText { get; }

    /// <summary>«18.4 MB در سطل زباله» — a sentence, with the quantity isolated inside it.</summary>
    public string HoldingText { get; }

    /// <summary>Already a sentence, in this request's language. Null when nothing has happened.</summary>
    public string? Notice { get; }

    /// <summary>
    /// The foot carries the total and the one irreversible control on the screen, so it is not drawn
    /// over an empty list: a button whose only outcome is «there was nothing to delete» teaches a
    /// customer that the control does nothing.
    /// </summary>
    public bool HasAnything => Rows.Count > 0;
}
