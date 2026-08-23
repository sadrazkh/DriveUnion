using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The sessionless path: a numeric Telegram sender id in, a tenant and a role out.
///
/// <para>There is no tenant argument on this class and there must never be one. Every Telegram
/// update arrives with no cookie, no principal and no tenant — there is nobody to take one from —
/// so a reader that accepted a tenant would be handed <c>Guid.Empty</c> by its only caller and would
/// resolve every bound customer to nothing while their rows sat plainly in the table. This is the
/// third anonymous surface in the product after <c>/d/{slug}</c> and the OAuth callback, and it is
/// the first one where the wrong answer is somebody else's file list rather than a 404.</para>
///
/// <para>The tenant and the role are joined from <c>AppUser</c> on every call. Nothing is cached and
/// nothing is denormalised onto the mapping row: a copy would go stale the day a customer moves or
/// is removed, silently, and the bot would keep answering out of the old tenant.</para>
/// </summary>
public sealed class TelegramIdentityReader(DriveUnionDbContext db) : ITelegramIdentityReader
{
    public async Task<TelegramIdentity?> ResolveAsync(
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        var row = await db.TelegramAccounts
            .AsNoTracking()
            .Where(a => a.TelegramUserId == telegramUserId)
            .Join(
                db.Users.AsNoTracking(),
                account => account.AppUserId,
                user => user.Id,
                (account, user) => new { account.AppUserId, user.TenantId, user.IsOperator })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return null;

        // Operator staff have no tenant, and the bot is a customer's surface: there is no tenant to
        // answer with, so the sender resolves to nobody and gets the stranger's reply. Refusing here
        // as well as at link time is deliberate — the two checks guard different moments, and this
        // one is the moment that decides which files a chat may read.
        if (row.TenantId is not { } tenantId || tenantId == Guid.Empty || row.IsOperator) return null;

        return new TelegramIdentity(row.AppUserId, tenantId, RoleOf());
    }

    /// <summary>
    /// The role, read from the user row on every update rather than cached on the mapping.
    ///
    /// <para>Today <c>AppUser</c> carries no role column, because roles inside a tenant are M5's and
    /// M5 has not landed. Until it does, every member of a tenant may do everything the panel offers
    /// a tenant — upload, delete, create and revoke links — so <see cref="TenantRole.Owner"/> is not
    /// a placeholder value, it is an accurate reading of what a tenant user can do in this product
    /// right now. When M5 adds the column this method reads it and nothing else changes; that is why
    /// it is one method rather than an expression inlined into the query.</para>
    /// </summary>
    private static TenantRole RoleOf() => TenantRole.Owner;
}
