using System.ComponentModel.DataAnnotations;
using DriveUnion.Core.Settings;
using DriveUnion.Infrastructure.Settings;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// The retention window as the operator's form posts it.
///
/// <para>An explicit request type, never the <c>OperatorSettings</c> entity. <c>Id</c>,
/// <c>UpdatedAt</c> and <c>UpdatedByUserId</c> are on no shape a request can bind to, so an
/// over-posted field has nothing to land on — the row's identity is a constant and who changed it is
/// read from the principal.</para>
/// </summary>
public sealed class RetentionForm
{
    /// <summary>
    /// Whole days. The range is also enforced in the controller and clamped again in the store: the
    /// attribute is what stops a mistyped figure at the door, and the clamp is what keeps the screen
    /// and the sweeper reading the same answer out of a row somebody edited by hand.
    /// </summary>
    [Range(OperatorSettings.MinimumTrashRetentionDays, OperatorSettings.MaximumTrashRetentionDays)]
    public int Days { get; set; } = OperatorSettings.DefaultTrashRetentionDays;
}

/// <summary>
/// «تنظیمات پنل» — the settings that belong to no workspace.
///
/// <para>There is one so far, and it is the one this phase needed: how long the trash keeps a file
/// before the purge may take it. The screen's largest job is not collecting a number — it is saying,
/// where the operator is typing, that the number reaches only what is deleted from now on. An
/// operator who lowers it expecting yesterday's deletions to go tonight has been told something
/// untrue by the absence of that sentence.</para>
/// </summary>
public sealed class OperatorSettingsPageViewModel
{
    public OperatorSettingsPageViewModel(
        StoredOperatorSettings settings,
        string? notice = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The stored value is already the one in force — the store clamps on the way out — so the
        // box opens on the number that will actually be stamped on the next deletion.
        Values = new RetentionForm { Days = settings.TrashRetentionDays };

        LastChangedText = settings.UpdatedAt is { } changed
            ? UiText.OperatorSettings.LastChanged(DisplayFormats.PanelDateTime(changed))
            : UiText.OperatorSettings.NeverChanged;

        Notice = notice;
        Error = error;
    }

    public RetentionForm Values { get; }

    public static int Minimum => OperatorSettings.MinimumTrashRetentionDays;

    public static int Maximum => OperatorSettings.MaximumTrashRetentionDays;

    public static string BoundsText => UiText.OperatorSettings.RetentionBounds(Minimum, Maximum);

    public string LastChangedText { get; }

    /// <summary>Already a sentence, in this request's language. Null when nothing has happened.</summary>
    public string? Notice { get; }

    /// <summary>Already a sentence. Null when nothing was refused.</summary>
    public string? Error { get; }
}
