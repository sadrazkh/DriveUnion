using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

internal static class DbTransactions
{
    /// <summary>
    /// Opens a transaction, or returns null when the caller already owns one.
    ///
    /// EF throws rather than nesting, and a request that has already begun a transaction wants its
    /// own commit point rather than ours — so the caller-owned case is a no-op here and the outer
    /// scope still gets all-or-nothing.
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginIfNoneAsync(
        DbContext db,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null) return null;

        return await db.Database.BeginTransactionAsync(cancellationToken);
    }
}
