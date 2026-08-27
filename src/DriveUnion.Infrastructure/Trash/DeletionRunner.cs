using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Trash;

/// <summary>
/// The Drive moves a queued deletion still owes, one file at a time.
///
/// <para><b>It has no tenant anywhere in it</b>, like <see cref="TrashPurge"/>: it runs with no
/// request, no cookie and no principal, over rows that each carry their own. A tenant-scoped read
/// from here would be handed <c>Guid.Empty</c> and would move nothing, for ever, silently.</para>
///
/// <para><b>Nothing here can lose a file.</b> Every row it touches already says deleted, is already
/// in the customer's trash and already has a purge deadline. What this adds is the move into the
/// operator's own trash folder, so that a restore has somewhere to come back from and the pool's
/// folders mean what they say. A file it never manages to move is still deleted, still restorable —
/// from where it is, which <c>TrashService</c> handles — and still destroyed on time by the purge,
/// which deletes by Drive id and does not care which folder the file was in.</para>
/// </summary>
public sealed class DeletionRunner(
    DriveUnionDbContext db,
    ITrashMover trash,
    TimeProvider clock,
    ILogger<DeletionRunner> logger) : IDeletionRunner
{
    /// <summary>What one file's move can end as. The third is why it is not a <c>bool</c>.</summary>
    private enum MoveOutcome
    {
        Moved,

        /// <summary>Drive refused this file. The pile behind it is still worth trying.</summary>
        Refused,

        /// <summary>Drive is refusing everybody. Stop, and do not count it against the file.</summary>
        RateLimited,
    }

    public async Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken)
    {
        if (budget <= 0) return 0;

        var job = await NextDueAsync(cancellationToken);
        if (job is null) return 0;

        if (job.Status == DeletionJobStatus.Pending)
        {
            job.Status = DeletionJobStatus.Running;
            await db.SaveChangesAsync(cancellationToken);
        }

        var moved = 0;

        for (var i = 0; i < budget && !cancellationToken.IsCancellationRequested; i++)
        {
            var file = await NextFileAsync(job, cancellationToken);

            if (file is null)
            {
                // A pass found nothing this job can still act on. Not «no files exist» — a file
                // somebody restored, and a file this job gave up on, are both gone from the query
                // that finds work, and finished is exactly «there is nothing left to find».
                await FinishAsync(job, cancellationToken);
                break;
            }

            var outcome = await MoveOneAsync(job, file, cancellationToken);

            if (outcome == MoveOutcome.Moved) moved++;

            if (outcome == MoveOutcome.RateLimited)
            {
                // Stopped rather than pushed through, the same call TrashPurgeRunner makes: past
                // this point the housekeeping is spending the request budget the customers are
                // uploading with, and the job is in no hurry — nothing a customer can see is waiting
                // on it.
                break;
            }
        }

        return moved;
    }

    /// <summary>The oldest job that still has work, running ones before pending ones.</summary>
    private async Task<DeletionJob?> NextDueAsync(CancellationToken cancellationToken)
    {
        var live = await db.DeletionJobs
            .Where(j => j.Status == DeletionJobStatus.Running || j.Status == DeletionJobStatus.Pending)
            .ToListAsync(cancellationToken);

        // In memory, because SQLite will not ORDER BY a DateTimeOffset and this has to behave the
        // same on it as on Postgres. There are a handful of these rows at any moment: each one is
        // finished within minutes of being made.
        return live
            .OrderBy(j => j.Status == DeletionJobStatus.Running ? 0 : 1)
            .ThenBy(j => j.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The next file this job still owes a move.
    ///
    /// <para>Asked again on every file rather than taken from a list: a file the customer restored
    /// while the job was running has had its claim cleared, and this is what makes «restore» beat a
    /// job that has not reached it yet without either of them knowing about the other.</para>
    ///
    /// <para>Still tenant-blind, and correctly so — the claim is a job id, and a job id is already as
    /// narrow as one workspace.</para>
    /// </summary>
    private Task<StoredFile?> NextFileAsync(DeletionJob job, CancellationToken cancellationToken) =>
        db.StoredFiles
            .Where(f => f.PendingDeletionJobId == job.Id
                && f.DeletedAt != null
                && f.DeletionAttempts < DeletionJob.MaxAttemptsPerFile)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<MoveOutcome> MoveOneAsync(
        DeletionJob job,
        StoredFile file,
        CancellationToken cancellationToken)
    {
        file.DeletionAttempts++;

        try
        {
            var placement = await trash.ToTrashAsync(
                job.TenantId,
                file.GoogleAccountId,
                file.OwnerUserId,
                file.DriveFileId,
                file.DriveFolderId,
                cancellationToken);

            file.DriveFolderId = placement.TrashFolderId;
            file.RestoreFolderId = placement.RestoreFolderId;

            // The placement's own deadline is thrown away, and that is the point: the file's was
            // stamped when the customer pressed delete, and re-stamping it here would move a
            // retention window by however long this queue happened to be.
            file.PendingDeletionJobId = null;
            file.DeletionAttempts = 0;

            job.FilesMoved++;

            // The file and the job in one SaveChanges. Apart, a crash between them either counts a
            // move that did not happen or leaves a file claimed by a job that has stopped counting.
            await db.SaveChangesAsync(cancellationToken);

            return MoveOutcome.Moved;
        }
        catch (DriveRateLimitedException exception)
        {
            // Not this file's fault, so not this file's attempt. Counting it would spend a whole
            // job's three tries on a bad ten minutes at Google and leave five thousand files sitting
            // in the wrong folder for ever.
            file.DeletionAttempts--;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                exception,
                "Deletion job {JobId} paused: Drive is rate limiting, and the remaining request "
                + "budget belongs to uploads.",
                job.Id);

            return MoveOutcome.RateLimited;
        }
        catch (Exception exception) when (exception is DriveApiException or IOException)
        {
            job.FailureReason = Trimmed(exception.Message);

            if (file.DeletionAttempts >= DeletionJob.MaxAttemptsPerFile)
            {
                // Given up on, and counted. The file stays deleted and stays claimed: the claim is
                // now the record of a move that never happened, and it is the only thing that says
                // so once the job row reads Completed.
                job.FilesFailed++;

                logger.LogError(
                    exception,
                    "Gave up moving deleted file {StoredFileId} into the trash after {Attempts} "
                    + "attempts. It is still deleted and will still be purged on its own deadline.",
                    file.Id,
                    file.DeletionAttempts);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "Attempt {Attempt} to move deleted file {StoredFileId} into the trash failed.",
                    file.DeletionAttempts,
                    file.Id);
            }

            await db.SaveChangesAsync(cancellationToken);

            return MoveOutcome.Refused;
        }
    }

    private async Task FinishAsync(DeletionJob job, CancellationToken cancellationToken)
    {
        job.Status = DeletionJobStatus.Completed;
        job.FinishedAt = clock.GetUtcNow();

        logger.LogInformation(
            "Deletion job {JobId} finished: {Moved} of {Total} file(s) moved into the trash, "
            + "{Failed} left where they were.",
            job.Id,
            job.FilesMoved,
            job.FilesTotal,
            job.FilesFailed);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Trimmed(string message) =>
        message.Length <= DeletionJob.MaxFailureReasonLength
            ? message
            : message[..DeletionJob.MaxFailureReasonLength];
}
