using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// Two <c>COUNT(*)</c>s, and nothing that could grow into a directory.
///
/// The queries here read across every tenant, which is correct — the bot is the operator's — and is
/// exactly why they are counts. Anything that returned rows would put customers' Telegram identities
/// on an operator's screen, and that is refused by the design rather than merely unbuilt.
/// </summary>
public sealed class TelegramOperatorView(DriveUnionDbContext db, TimeProvider clock)
    : ITelegramOperatorView
{
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

        return new TelegramOperatorHealth(linked, pending.Count(e => e > now));
    }
}
