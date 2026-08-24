namespace DriveUnion.Core.Settings;

/// <summary>
/// The operator's own knobs. One row, seeded by the migration that created the table, in the same
/// shape as <c>TelegramBotSettings</c>.
///
/// <para>A table rather than configuration, because these are answers an operator changes by
/// pressing something and expects to still be true after a deploy — and this product has already
/// lost a setting to a redeploy once, when the Google OAuth client lived in a file inside the
/// container.</para>
///
/// <para>A table rather than a fifth number on <see cref="Core.Plans.Plan"/>, which was the other
/// candidate: retention per tier would ride the existing plan machinery and be more flexible, but it
/// would thread a new figure through the catalogue, the tenant columns, the editor, the copy-on-
/// assign and the quota history for a value nobody has yet asked to differ per customer. When
/// somebody does, moving it there is a migration and a copy, and this row becomes the default.</para>
/// </summary>
public sealed class OperatorSettings
{
    /// <summary>Always 1. The table holds one row and the migration puts it there.</summary>
    public const int SingletonId = 1;

    /// <summary>Drive's own number, and Dropbox's, so it needs no explaining on the screen.</summary>
    public const int DefaultTrashRetentionDays = 30;

    /// <summary>
    /// Below this the trash stops being a safety net and becomes a delay. An operator who wants
    /// deletion to be immediate should say so with the empty-trash button, not by setting this to
    /// something that looks like a retention policy and is not one.
    /// </summary>
    public const int MinimumTrashRetentionDays = 1;

    /// <summary>
    /// A year. Past this the pool is being spent on files nobody has looked at since last year, and
    /// the number stops being a promise to the customer and starts being an unpaid storage bill.
    /// </summary>
    public const int MaximumTrashRetentionDays = 365;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// How long a deleted file waits in the trash before the purge may take it.
    ///
    /// <para>Read when a file is deleted and written onto that file's own deadline, never consulted
    /// again for it. So lowering this shortens the wait for what is deleted next, and cannot reach
    /// back and destroy what somebody deleted yesterday expecting a month.</para>
    /// </summary>
    public int TrashRetentionDays { get; set; } = DefaultTrashRetentionDays;

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Clamped rather than rejected, because a stored row is not a form and has no reader.</summary>
    public int EffectiveRetentionDays => Math.Clamp(
        TrashRetentionDays,
        MinimumTrashRetentionDays,
        MaximumTrashRetentionDays);
}
