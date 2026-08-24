using DriveUnion.Core.Settings;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Settings;

/// <summary>
/// The operator's knobs as a screen sees them.
///
/// <para><see cref="TrashRetentionDays"/> is the value <b>in force</b>, which is the stored number
/// clamped to the range the row itself declares. A screen shows the number that will actually be
/// stamped on the next deletion rather than whatever happens to be in the column.</para>
/// </summary>
public sealed record StoredOperatorSettings(
    int TrashRetentionDays,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedByUserId);

/// <summary>
/// Reading and writing the one <c>OperatorSettings</c> row.
///
/// <para>The same discipline as <c>ITelegramBotSettingsStore</c>: one accessor, so a setting an
/// operator changes by pressing something has exactly one place it is read from and exactly one
/// place it is written.</para>
/// </summary>
public interface IOperatorSettingsStore
{
    Task<StoredOperatorSettings> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new retention window and returns what was actually stored.
    ///
    /// <para>Clamped rather than refused, because <see cref="OperatorSettings"/> clamps on the way
    /// out too and a store that let an out-of-range number sit in the column would leave the screen
    /// and the sweeper reading different answers from one row. The returned value is what is now in
    /// force, so a form that wants to say «that was out of range» compares it against what it
    /// sent.</para>
    /// </summary>
    Task<StoredOperatorSettings> SaveTrashRetentionAsync(
        int retentionDays,
        Guid? updatedByUserId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOperatorSettingsStore"/>
public sealed class OperatorSettingsStore(
    DriveUnionDbContext db,
    TimeProvider clock,
    ILogger<OperatorSettingsStore> logger) : IOperatorSettingsStore
{
    public async Task<StoredOperatorSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: false, cancellationToken);

        // The migration seeds the row, so its absence means somebody removed it. The defaults are
        // the row's own, which keeps «no row» and «a fresh row» the same answer rather than making
        // the trash behave differently on a database somebody has been editing by hand.
        return row is null
            ? new StoredOperatorSettings(OperatorSettings.DefaultTrashRetentionDays, null, null)
            : new StoredOperatorSettings(row.EffectiveRetentionDays, row.UpdatedAt, row.UpdatedByUserId);
    }

    public async Task<StoredOperatorSettings> SaveTrashRetentionAsync(
        int retentionDays,
        Guid? updatedByUserId,
        CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: true, cancellationToken);

        if (row is null)
        {
            // Written back rather than refused, for the reason the Telegram store gives: this screen
            // exists so the setting can be changed without a terminal on the box, and a missing row
            // is the one state where that would stop being true.
            row = new OperatorSettings { Id = OperatorSettings.SingletonId };
            db.OperatorSettings.Add(row);
        }

        row.TrashRetentionDays = Math.Clamp(
            retentionDays,
            OperatorSettings.MinimumTrashRetentionDays,
            OperatorSettings.MaximumTrashRetentionDays);
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedByUserId = updatedByUserId;

        await db.SaveChangesAsync(cancellationToken);

        // Worth a line because it is the only setting in the product that decides when somebody
        // else's bytes are destroyed, and «when did retention change» is the first question after a
        // file goes missing sooner than its owner expected.
        logger.LogInformation(
            "The trash retention window was set to {Days} day(s).",
            row.TrashRetentionDays);

        return new StoredOperatorSettings(row.EffectiveRetentionDays, row.UpdatedAt, row.UpdatedByUserId);
    }

    private Task<OperatorSettings?> RowAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.OperatorSettings : db.OperatorSettings.AsNoTracking();

        return query.FirstOrDefaultAsync(s => s.Id == OperatorSettings.SingletonId, cancellationToken);
    }
}
