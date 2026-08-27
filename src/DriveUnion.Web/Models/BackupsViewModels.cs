using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One copy of a snapshot, as a line in the table's last column.
///
/// <para>A copy the pruner has taken is still drawn, greyed rather than dropped: «there was one on
/// A2 and it has been rotated out» is a different sentence from «there has never been one», and only
/// one of them is evidence the backup is working.</para>
/// </summary>
public sealed record BackupCopyRow(string Label, string Email, bool IsInThePool)
{
    public string Text => IsInThePool
        ? UiText.Backups.CopyOn(Label)
        : UiText.Backups.CopyRemoved(Label);

    /// <summary>The full address in a tooltip; the column has room for a two-character handle.</summary>
    public string Title => Email;
}

/// <summary>One run, as the operator's table draws it.</summary>
public sealed record BackupRow(
    string Name,
    CatalogueSnapshotStatus Status,
    bool ByHand,
    DateTimeOffset RequestedAt,
    int FileCount,
    int TenantCount,
    int EncryptionCount,
    long SizeBytes,
    string? FailureReason,
    IReadOnlyList<BackupCopyRow> Copies)
{
    /// <summary>
    /// The status in words.
    ///
    /// <para>Mapped here rather than by a <c>UiText</c> entry taking the enum: the catalogue test
    /// renders every entry in both languages and cannot supply one, so an entry with an enum
    /// parameter is an entry nothing ever checks.</para>
    /// </summary>
    public string StatusText => Status switch
    {
        CatalogueSnapshotStatus.Pending => UiText.Backups.StatusPending,
        CatalogueSnapshotStatus.Running => UiText.Backups.StatusRunning,
        CatalogueSnapshotStatus.Completed => UiText.Backups.StatusCompleted,
        _ => UiText.Backups.StatusFailed,
    };

    /// <summary>The panel's own badge scale — plain is good, and the other two are the comp's.</summary>
    public string StatusClass => Status switch
    {
        CatalogueSnapshotStatus.Completed => "badge",
        CatalogueSnapshotStatus.Failed => "badge badge--danger",
        _ => "badge badge--muted",
    };

    public string TriggerText => ByHand ? UiText.Backups.ByHand : UiText.Backups.Scheduled;

    public string TakenText => DisplayFormats.PanelDateTime(RequestedAt);

    public string ContentsText => UiText.Backups.Contents(FileCount, TenantCount);

    public string? EncryptedText => EncryptionCount > 0
        ? UiText.Backups.Encrypted(EncryptionCount)
        : null;

    /// <summary>Latin in both languages, like every other byte quantity in the panel.</summary>
    public string SizeText => DisplayFormats.Bytes(SizeBytes);

    public bool HasCopies => Copies.Count > 0;

    public static BackupRow From(CatalogueSnapshotView snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new BackupRow(
            snapshot.Name,
            snapshot.Status,
            snapshot.ByHand,
            snapshot.RequestedAt,
            snapshot.FileCount,
            snapshot.TenantCount,
            snapshot.EncryptionCount,
            snapshot.SizeBytes,
            snapshot.FailureReason,
            [.. snapshot.Copies.Select(c => new BackupCopyRow(c.AccountLabel, c.AccountEmail, c.IsInThePool))]);
    }
}

/// <summary>
/// The operator's backup screen.
///
/// <para>The warning at the top is the reason the page is worth opening at all: a backup that
/// stopped working three months ago looks exactly like one that is working, right up until the
/// morning somebody needs it.</para>
/// </summary>
public sealed record BackupsPageViewModel(
    IReadOnlyList<BackupRow> Snapshots,
    DateTimeOffset? NewestGoodAt,
    DateTimeOffset Now,
    string? Notice,
    string? Error)
{
    /// <summary>The constants the worker actually runs on, said on the screen rather than guessed.</summary>
    public static string ScheduleText => UiText.Backups.ScheduleBody(
        (int)CatalogueSnapshot.Interval.TotalDays,
        CatalogueSnapshot.Keep,
        CatalogueSnapshot.Copies);

    public bool IsEmpty => Snapshots.Count == 0;

    /// <summary>Whole days, floored: «four days old» is the honest reading of ninety-nine hours.</summary>
    public int? AgeInDays => NewestGoodAt is { } when ? (int)(Now - when).TotalDays : null;

    /// <summary>
    /// The sentence above the table, or null when the newest snapshot is younger than the interval
    /// it is taken on — which is the ordinary state and needs no comment.
    /// </summary>
    public string? Warning => AgeInDays switch
    {
        null when Snapshots.Count > 0 => UiText.Backups.NeverWarning,
        null => null,

        // Twice the interval before anybody is told off. One missed night is a rate limit or a
        // redeploy and the next pass fixes it; two is a pattern, and a page that cries on the first
        // is a page an operator learns to scroll past.
        { } days when days >= CatalogueSnapshot.Interval.TotalDays * 2 =>
            UiText.Backups.StaleWarning(days),
        _ => null,
    };
}
