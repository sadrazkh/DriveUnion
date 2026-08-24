using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// Counts, and nothing that could grow into a directory.
///
/// The queries here read across every tenant, which is correct — the bot is the operator's — and is
/// exactly why they are counts. Anything that returned rows would put customers' Telegram identities
/// on an operator's screen, and that is refused by the design rather than merely unbuilt.
/// </summary>
public sealed class TelegramOperatorView(
    DriveUnionDbContext db,
    TelegramWorkDirectory workDirectory,
    IOptions<TelegramOptions> options,
    TimeProvider clock) : ITelegramOperatorView
{
    private readonly TelegramOptions _options = options.Value;

    public async Task<TelegramOperatorHealth> ReadAsync(CancellationToken cancellationToken)
    {
        var linked = await db.TelegramAccounts.CountAsync(cancellationToken);

        // Unconsumed rows, with the expired ones dropped in memory: SQLite will not compare a
        // DateTimeOffset, and this figure is read once per page view over a table of ten-minute rows.
        var now = clock.GetUtcNow();
        var pending = await db.TelegramLinkTokens
            .AsNoTracking()
            .Where(t => t.ConsumedAt == null)
            .Select(t => t.ExpiresAt)
            .ToListAsync(cancellationToken);

        var depth = await db.TelegramOutbox
            .CountAsync(
                o => o.Status == TelegramOutboxStatus.Pending || o.Status == TelegramOutboxStatus.Claimed,
                cancellationToken);

        var yesterday = now - TimeSpan.FromDays(1);

        var updates = await db.TelegramUpdatesSeen
            .AsNoTracking()
            .Select(u => u.ReceivedAt)
            .ToListAsync(cancellationToken);

        var failures = await db.TelegramOutbox
            .AsNoTracking()
            .Where(o => o.Status == TelegramOutboxStatus.Failed)
            .Select(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        return new TelegramOperatorHealth(
            linked,
            pending.Count(e => e > now),
            depth,
            updates.Count(u => u >= yesterday),
            failures.Count(f => f >= yesterday));
    }

    public TelegramServerHealth ReadServerHealth()
    {
        // The bot id is read from the settings row by the caller in the general case; here it is
        // taken from the same options-derived path the sweeper uses, so the two can never disagree
        // about which directory is being measured.
        var botUserId = db.TelegramBotSettings
            .AsNoTracking()
            .Where(s => s.Id == TelegramBotSettings.SingletonId)
            .Select(s => s.BotUserId)
            .FirstOrDefault();

        var (bytes, files, oldest) = workDirectory.Measure(botUserId);

        return new TelegramServerHealth(
            _options.ApiBaseUrl,
            _options.LocalBotServer,
            workDirectory.PathFor(botUserId),
            bytes,
            files,
            oldest,
            workDirectory.FreeBytes(botUserId));
    }
}
