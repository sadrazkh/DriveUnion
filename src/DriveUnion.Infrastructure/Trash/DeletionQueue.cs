using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// Deleting more than one thing: the half that happens while the customer is still looking at it.
///
/// <para><b>Everything visible, in one transaction, in a fixed number of statements.</b> The rows are
/// stamped deleted, the deadline is written, the links are revoked and a deleted folder's rows go —
/// four statements for four files or for forty thousand. What is left over is one Drive move per
/// file, which no customer can see and which <see cref="DeletionRunner"/> owes.</para>
///
/// <para><b>Why the order is inverted here.</b> <c>FileCatalog.DeleteAsync</c> moves the file in
/// Drive before it stamps the row, because a row stamped over a move that never happened would leave
/// the bytes in the customer's home folder with nothing left to retry. The job row is that retry —
/// see <see cref="DeletionJob"/> — and the purge deletes by Drive id regardless of which folder the
/// file ended up in, so the worst a never-completed move costs is tidiness inside the operator's own
/// Drive.</para>
///
/// <para>Every query carries <c>tenantId</c>, because this model has no global query filter and must
/// not have one — see <see cref="DriveUnionDbContext.OnModelCreating"/>.</para>
/// </summary>
public sealed class DeletionQueue(
    DriveUnionDbContext db,
    IOperatorSettingsStore settings,
    TimeProvider clock) : IDeletionQueue
{
    public async Task<DeletionResult> DeleteFilesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        if (fileIds.Count == 0) return new DeletionResult(true, 0, 0, null);

        var jobId = Guid.CreateVersion7();

        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        // The tenant predicate is on the UPDATE itself rather than on a read before it, so another
        // workspace's file is not «found and then rejected» — it is never matched. A file already in
        // the trash is not matched either, which is what keeps its own deadline where it was.
        var claimed = await ClaimAsync(
            tenantId,
            db.StoredFiles.Where(f => f.TenantId == tenantId && fileIds.Contains(f.Id)),
            jobId,
            cancellationToken);

        if (claimed.Count == 0)
        {
            // Nothing was this workspace's, or it had all gone already. No job, because a worker with
            // nothing to move is a row somebody has to explain.
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            return new DeletionResult(true, 0, 0, null);
        }

        await QueueAsync(tenantId, jobId, DeletionScope.Selection, null, claimed.Count, cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return new DeletionResult(true, claimed.Count, 0, jobId);
    }

    public async Task<DeletionResult> DeleteFolderAsync(
        Guid tenantId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        // The whole workspace's folders, read once and walked in memory — the same trade
        // <see cref="Repositories.FolderTree"/> makes and for the same reason: a subtree in SQL is a
        // query per level or a recursive CTE written twice, once per provider, and a workspace's
        // folders are tens of rows.
        var all = await db.Folders
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .Select(f => new { f.Id, f.ParentFolderId, f.Name })
            .ToListAsync(cancellationToken);

        if (all.FirstOrDefault(f => f.Id == folderId) is not { } root) return DeletionResult.NotFound;

        var byParent = all
            .Where(f => f.ParentFolderId is not null)
            .ToLookup(f => f.ParentFolderId!.Value, f => f.Id);

        // Level by level rather than by recursion, because the levels are what the deletion below
        // needs anyway: the self-referencing foreign key is Restrict, so a parent cannot go while a
        // child of it is still there, and the order is the deepest level first.
        List<List<Guid>> levels = [[root.Id]];

        for (var depth = 0; depth < Folder.MaxDepth && levels[^1].Count > 0; depth++)
        {
            var next = levels[^1].SelectMany(id => byParent[id]).ToList();
            if (next.Count == 0) break;

            levels.Add(next);
        }

        var subtree = levels.SelectMany(level => level).Select(id => (Guid?)id).ToList();
        var jobId = Guid.CreateVersion7();

        await using var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        var claimed = await ClaimAsync(
            tenantId,
            db.StoredFiles.Where(f => f.TenantId == tenantId && subtree.Contains(f.FolderId)),
            jobId,
            cancellationToken);

        if (claimed.Count > 0)
        {
            await QueueAsync(
                tenantId, jobId, DeletionScope.Folder, root.Name, claimed.Count, cancellationToken);
        }

        // The folders themselves, deepest first. They go now rather than when the job finishes: a
        // folder left standing while its contents drain is a folder the customer can still upload
        // into, and this job would then take the file they had just put there.
        for (var depth = levels.Count - 1; depth >= 0; depth--)
        {
            var level = levels[depth];
            if (level.Count == 0) continue;

            await db.Folders
                .Where(f => f.TenantId == tenantId && level.Contains(f.Id))
                .ExecuteDeleteAsync(cancellationToken);

            Forget<Folder>(f => level.Contains(f.Id));
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return new DeletionResult(
            true, claimed.Count, subtree.Count, claimed.Count > 0 ? jobId : null);
    }

    public async Task<IReadOnlyList<DeletionJobView>> LiveAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var live = await db.DeletionJobs
            .AsNoTracking()
            .Where(j => j.TenantId == tenantId
                && (j.Status == DeletionJobStatus.Pending || j.Status == DeletionJobStatus.Running))
            .ToListAsync(cancellationToken);

        // Ordered in memory: SQLite will not ORDER BY a DateTimeOffset, and this layer runs on SQLite
        // in the tests and Postgres in production. A workspace has none of these rows nearly always
        // and a handful at worst.
        return
        [
            .. live
                .OrderBy(j => j.CreatedAt)
                .Select(j => new DeletionJobView(
                    j.Id,
                    j.Scope,
                    j.FolderName,

                    // Given up on counts as done. The worker will not come back to those files, so
                    // leaving them out of the figure would leave a job reading «۴۹۹۸ از ۵۰۰۰» for
                    // ever on a screen that is telling somebody they can stop waiting.
                    j.FilesMoved + j.FilesFailed,
                    j.FilesTotal)),
        ];
    }

    /// <summary>
    /// Stamps every live file the query matches as deleted, marks it as owing a move, revokes its
    /// links, and answers with the ids it took.
    ///
    /// <para>Two statements and one read, whatever the size of the pile. The deadline is taken from
    /// the retention window in force at this moment and never recomputed — the same promise
    /// <c>TrashMover</c> makes, so lowering the setting tomorrow cannot reach back and shorten what
    /// somebody deleted today expecting a month.</para>
    ///
    /// <para><see cref="StoredFile.RestoreFolderId"/> is deliberately <b>not</b> written here. It
    /// means «the folder Drive would have to put this back into, because we took it out of there»,
    /// and nothing has taken it out of anywhere yet. Left null, a restore before the worker arrives
    /// takes the branch <c>TrashService</c> already has for a file nothing ever moved — no Drive
    /// call, because the file is still exactly where a restore would put it.</para>
    ///
    /// <para>Deleting a file revokes its links in the same breath — leaving them active would let
    /// <c>/d/{slug}</c> keep answering for a file the workspace removed, for as long as the queue
    /// happened to be. Restoring does not undo that, which is <c>FileCatalog</c>'s rule and not a new
    /// one here. The links are revoked by what was <i>claimed</i> rather than by what the caller
    /// asked for: another workspace's ids and files already in the trash are not matched, and
    /// revoking by the asking list would touch links this job has nothing to do with.</para>
    /// </summary>
    private async Task<List<Guid>> ClaimAsync(
        Guid tenantId,
        IQueryable<StoredFile> live,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var retentionDays = (await settings.ReadAsync(cancellationToken)).TrashRetentionDays;

        // Worked out here rather than inside the SetProperty: an expression that arithmetics on a
        // date is one EF tries to translate, and date arithmetic is exactly where the two providers
        // stop agreeing. A value it can only parameterise is a value that means the same thing on
        // both.
        var purgeAfter = now.AddDays(retentionDays);

        var affected = await live
            .Where(f => f.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.DeletedAt, now)
                    .SetProperty(f => f.PurgeAfter, purgeAfter)
                    .SetProperty(f => f.PendingDeletionJobId, jobId)

                    // Reset rather than left alone: a file that failed a previous job and was
                    // restored and deleted again deserves its three tries afresh.
                    .SetProperty(f => f.DeletionAttempts, 0),
                cancellationToken);

        if (affected == 0) return [];

        // Read back rather than derived from the caller's list, because what was claimed is not what
        // was asked for. Projected, so nothing is tracked by asking.
        var claimed = await db.StoredFiles
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.PendingDeletionJobId == jobId)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        await db.ShareLinks
            .Where(l => l.TenantId == tenantId && l.IsActive && claimed.Contains(l.StoredFileId))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), cancellationToken);

        Forget<StoredFile>(f => claimed.Contains(f.Id));
        Forget<ShareLink>(l => claimed.Contains(l.StoredFileId));

        return claimed;
    }

    /// <summary>
    /// Drops this scope's own copies of the rows the statements above went round.
    ///
    /// <para><c>ExecuteUpdate</c> and <c>ExecuteDelete</c> do not go through the change tracker, so a
    /// copy somebody in this scope already loaded still says the file is live and the folder is
    /// there. Two things go wrong if it is left attached, and the second is the dangerous one: a
    /// later read in the same scope is answered from the stale copy, and a later
    /// <c>SaveChanges</c> over it compares against the values it was loaded with — so it decides the
    /// claim column is unchanged and writes the claim back out of existence, leaving a file marked
    /// deleted that no worker will ever find.</para>
    ///
    /// <para><c>TenantStorageMeter</c> detaches for exactly this reason and says so; this is the same
    /// hazard on a bigger table. It is not «only a test problem» because the worker gets a fresh
    /// scope in production: the queue is reachable from a request, and a request that deleted a
    /// selection and then read one of those files back would be reading the wrong answer.</para>
    /// </summary>
    private void Forget<T>(Func<T, bool> matching) where T : class
    {
        foreach (var entry in db.ChangeTracker.Entries<T>().Where(e => matching(e.Entity)).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task QueueAsync(
        Guid tenantId,
        Guid jobId,
        DeletionScope scope,
        string? folderName,
        int files,
        CancellationToken cancellationToken)
    {
        db.DeletionJobs.Add(new DeletionJob
        {
            Id = jobId,
            TenantId = tenantId,
            Scope = scope,
            FolderName = folderName,
            FilesTotal = files,
            Status = DeletionJobStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
