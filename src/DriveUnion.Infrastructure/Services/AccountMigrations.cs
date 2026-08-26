using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// The operator's view of the pool, and the button that starts a drain.
///
/// <para>No tenant appears anywhere in this class. The pool is the operator's, a customer must never
/// learn it exists, and every method here is reachable only from the operator panel — which is the
/// same rule <c>GoogleAccount</c> states about itself.</para>
/// </summary>
public sealed class AccountMigrations(DriveUnionDbContext db, TimeProvider clock) : IAccountMigrations
{
    public async Task<IReadOnlyList<AccountInventory>> InventoryAsync(CancellationToken cancellationToken)
    {
        var accounts = await db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.Email,
                a.Label,
                a.Status,
                a.QuotaTotalBytes,
                a.QuotaUsedBytes,
            })
            .ToListAsync(cancellationToken);

        // Live files only. What is in the trash still occupies the account and still costs the
        // operator, but it is on its way out — a drain that moved it would be spending Google's
        // bandwidth relocating something the purge is about to delete.
        var held = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.DeletedAt == null)
            .GroupBy(f => f.GoogleAccountId)
            .Select(g => new
            {
                AccountId = g.Key,
                FileCount = g.Count(),
                LiveBytes = g.Sum(f => f.SizeBytes),
                TenantCount = g.Select(f => f.TenantId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);

        var byAccount = held.ToDictionary(h => h.AccountId);

        return
        [
            .. accounts.Select(a =>
            {
                var has = byAccount.GetValueOrDefault(a.Id);

                return new AccountInventory(
                    a.Id,
                    a.Email,
                    a.Label,
                    a.Status,
                    a.QuotaTotalBytes,
                    a.QuotaUsedBytes,
                    has?.FileCount ?? 0,
                    has?.LiveBytes ?? 0,
                    has?.TenantCount ?? 0);
            }),
        ];
    }

    public async Task<MigrationStartResult> StartAsync(
        Guid sourceAccountId,
        Guid targetAccountId,
        CancellationToken cancellationToken)
    {
        if (sourceAccountId == targetAccountId)
        {
            return new MigrationStartResult(null, MigrationRefusal.SameAccount);
        }

        var source = await db.GoogleAccounts.FirstOrDefaultAsync(a => a.Id == sourceAccountId, cancellationToken);
        var target = await db.GoogleAccounts.FirstOrDefaultAsync(a => a.Id == targetAccountId, cancellationToken);

        if (source is null || target is null)
        {
            return new MigrationStartResult(null, MigrationRefusal.UnknownAccount);
        }

        if (target.Status != GoogleAccountStatus.Healthy)
        {
            // Not a technicality: a disconnected account cannot be written to at all, and a paused
            // one is paused because the operator said so. Draining into either would fail file by
            // file for hours before anybody noticed.
            return new MigrationStartResult(null, MigrationRefusal.TargetNotHealthy);
        }

        var running = await db.AccountMigrations.AnyAsync(
            m => m.SourceAccountId == sourceAccountId
                && (m.Status == AccountMigrationStatus.Pending
                    || m.Status == AccountMigrationStatus.Running),
            cancellationToken);

        if (running) return new MigrationStartResult(null, MigrationRefusal.AlreadyRunning);

        var needed = await db.StoredFiles
            .Where(f => f.GoogleAccountId == sourceAccountId && f.DeletedAt == null)
            .SumAsync(f => f.SizeBytes, cancellationToken);

        // A zero total means nobody has asked Google for this account's quota yet, which is not the
        // same as «no room» — the upload selector makes the same distinction and for the same
        // reason. Refusing on an unknown would block every drain until the first quota refresh.
        if (target.QuotaTotalBytes > 0 && target.QuotaTotalBytes - target.QuotaUsedBytes < needed)
        {
            return new MigrationStartResult(null, MigrationRefusal.TargetTooSmall);
        }

        // Pausing the source is part of accepting, not a separate thing to remember. The selector
        // skips anything that is not Healthy, so this is what stops new uploads landing on the
        // account being emptied — and without it the drain races them and never finishes.
        if (source.Status == GoogleAccountStatus.Healthy)
        {
            source.Status = GoogleAccountStatus.Paused;
        }

        var migration = new AccountMigration
        {
            Id = Guid.NewGuid(),
            SourceAccountId = sourceAccountId,
            TargetAccountId = targetAccountId,
            Status = AccountMigrationStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };

        db.AccountMigrations.Add(migration);
        await db.SaveChangesAsync(cancellationToken);

        return new MigrationStartResult(migration.Id, MigrationRefusal.None);
    }

    public async Task<IReadOnlyList<AccountMigrationView>> ListAsync(CancellationToken cancellationToken)
    {
        var migrations = await db.AccountMigrations.AsNoTracking().ToListAsync(cancellationToken);
        if (migrations.Count == 0) return [];

        var labels = await db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new { a.Id, a.Label, a.Email })
            .ToListAsync(cancellationToken);

        var nameOf = labels.ToDictionary(
            a => a.Id,
            a => string.IsNullOrWhiteSpace(a.Label) ? a.Email : a.Label);

        // How many are still on the source, counted now rather than stored: a progress figure kept
        // in a column is one that disagrees with the table the moment anything else touches a file.
        var remaining = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.DeletedAt == null)
            .GroupBy(f => f.GoogleAccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var leftOn = remaining.ToDictionary(r => r.AccountId, r => r.Count);

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset. See ShareLinkService.
        return
        [
            .. migrations
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new AccountMigrationView(
                    m.Id,
                    m.SourceAccountId,
                    nameOf.GetValueOrDefault(m.SourceAccountId, "—"),
                    m.TargetAccountId,
                    nameOf.GetValueOrDefault(m.TargetAccountId, "—"),
                    m.Status,
                    m.FilesMoved,
                    m.FilesFailed,
                    m.BytesMoved,
                    leftOn.GetValueOrDefault(m.SourceAccountId),
                    m.FailureReason,
                    m.CreatedAt,
                    m.FinishedAt)),
        ];
    }

    public async Task<bool> CancelAsync(Guid migrationId, CancellationToken cancellationToken)
    {
        var migration = await db.AccountMigrations.FirstOrDefaultAsync(
            m => m.Id == migrationId, cancellationToken);

        if (migration is null) return false;

        if (migration.Status is not (AccountMigrationStatus.Pending or AccountMigrationStatus.Running))
        {
            // Already over. Not an error and not a second cancellation — the caller asked for it to
            // stop and it has.
            return false;
        }

        migration.Status = AccountMigrationStatus.Cancelled;
        migration.FinishedAt = clock.GetUtcNow();

        // The source account stays paused. Un-pausing it here would send new uploads onto an account
        // half of whose files have just moved off it, which is not a decision this method is in a
        // position to make — the operator is.
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
