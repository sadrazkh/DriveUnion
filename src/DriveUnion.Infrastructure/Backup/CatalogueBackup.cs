using System.IO.Compression;
using System.Security.Cryptography;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Backup;

/// <summary>
/// Writes the catalogue into the pool the catalogue describes, so that losing Postgres stops being
/// the same thing as losing every file in the product.
///
/// <para><b>The failure.</b> Bytes are in Google's accounts and are as safe as Google. The mapping —
/// this workspace's «quarterly.mp4» is Drive file <c>1abc…</c> on account A2 — was in Postgres and
/// nowhere else, so a lost database left every byte intact and none of them reachable. This is the
/// export that was missing. See <see cref="CatalogueSnapshot"/> for the shape of the record and
/// <see cref="CatalogueSnapshotFormat"/> for the shape of the file.</para>
///
/// <para><b>Nothing is held.</b> A hundred thousand files is tens of megabytes of JSON, and it is
/// never in memory: rows stream out of the database one at a time, through gzip, into a buffer that
/// is emptied into Drive whenever it fills. What this costs is one chunk, whatever the size of the
/// catalogue — the same discipline the migrator and the remote fetcher follow, for the same
/// reason.</para>
///
/// <para><b>It reads across every tenant, and that is not a missing scope.</b> There are no global
/// query filters in this model and tenant scoping is an explicit argument everywhere; here the job
/// genuinely is «all of them at once», because the thing being backed up is the mapping from every
/// workspace to the operator's accounts. Nothing in this class is reachable from a tenant-facing
/// path, and the file it writes never enters a customer's tree.</para>
///
/// <para><b>Two copies, on two accounts.</b> A single copy answers «the database is gone» and not
/// «that account is gone», and the second of those takes the index of every other account's files
/// with it. See <see cref="CatalogueSnapshot.Copies"/>.</para>
/// </summary>
public sealed class CatalogueBackup(
    DriveUnionDbContext db,
    IDriveClient drive,
    IDriveFolders folders,
    TimeProvider clock,
    ILogger<CatalogueBackup> logger,
    int chunkSize = CatalogueBackup.DefaultChunkSize) : ICatalogueBackup
{
    /// <summary>
    /// What is pushed to Drive in one go, and a multiple of the 256 KiB a non-final chunk must be.
    ///
    /// <para>8 MiB, like the migrator's and the fetcher's. The buffer only ever grows to what a
    /// snapshot actually produces, so the ordinary case — a pool of a few thousand files, a few
    /// hundred kilobytes compressed — never allocates any of it and goes up in a single chunk.</para>
    /// </summary>
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    /// <summary>
    /// The chunk this instance actually uses.
    ///
    /// <para><b>Not configuration.</b> Nothing in the product passes it and there is no setting
    /// behind it; the parameter exists so a test can drive the path where a snapshot spans several
    /// chunks — the buffer draining, the bytes sliding to the front, every chunk but the last
    /// declaring no total — without seeding the hundred megabytes of rows it would otherwise take to
    /// fill 8 MiB of gzip. That path is the one that silently corrupts a backup if it is wrong, so
    /// «it is only exercised in production» was not an acceptable answer.</para>
    ///
    /// <para>Validated rather than trusted: Drive does not reject a chunk that is not a multiple of
    /// 256 KiB, it accepts it and quietly stops acknowledging bytes.</para>
    /// </summary>
    private readonly int _chunkSize = UploadChunking.IsValidChunkSize(chunkSize)
        ? chunkSize
        : throw new ArgumentOutOfRangeException(
            nameof(chunkSize),
            chunkSize,
            "A chunk has to be a multiple of 256 KiB within Drive's bounds, or the upload stalls "
            + "with no error anywhere.");

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = await NextDueAsync(cancellationToken);
        if (snapshot is null) return 0;

        var startedAt = clock.GetUtcNow();

        snapshot.Status = CatalogueSnapshotStatus.Running;
        snapshot.StartedAt = startedAt;
        snapshot.Attempts++;

        // Named for the moment the rows are read rather than the moment the button was pressed. The
        // name is a timestamp of the contents, and a snapshot that sat in the queue overnight
        // because the process was down would otherwise claim to describe yesterday.
        snapshot.Name = CatalogueSnapshotFormat.NameFor(startedAt);

        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var copies = await WriteAsync(snapshot, startedAt, cancellationToken);

            db.CatalogueSnapshotCopies.AddRange(copies);

            snapshot.CopiesMade = copies.Count;
            snapshot.Status = CatalogueSnapshotStatus.Completed;
            snapshot.FinishedAt = clock.GetUtcNow();
            snapshot.FailureReason = null;

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Catalogue snapshot {Name} written to {Copies} of {Wanted} account(s): {Files} files, "
                + "{Bytes} bytes compressed.",
                snapshot.Name,
                snapshot.CopiesMade,
                snapshot.CopiesWanted,
                snapshot.FileCount,
                snapshot.SizeBytes);

            return copies.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping. Back to Pending and the attempt handed back, so a redeploy in
            // the middle of the nightly run does not spend one of the three tries on it.
            snapshot.Status = CatalogueSnapshotStatus.Pending;
            snapshot.Attempts--;

            await db.SaveChangesAsync(CancellationToken.None);

            return 0;
        }
        catch (Exception exception) when (IsWorthRetrying(exception))
        {
            await FailAsync(snapshot, exception, cancellationToken);

            return 0;
        }
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var completed = await db.CatalogueSnapshots
            .Where(s => s.Status == CatalogueSnapshotStatus.Completed)
            .ToListAsync(cancellationToken);

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset, and this code has to
        // behave the same on it as on Postgres. The same reason ShareLinkService sorts its links
        // here rather than in the query.
        var stale = completed
            .OrderByDescending(s => s.FinishedAt ?? s.RequestedAt)
            .Skip(CatalogueSnapshot.Keep)
            .Select(s => s.Id)
            .ToList();

        if (stale.Count == 0) return 0;

        var doomed = await db.CatalogueSnapshotCopies
            .Where(c => stale.Contains(c.SnapshotId) && c.RemovedAt == null)
            .ToListAsync(cancellationToken);

        var removed = 0;

        foreach (var copy in doomed)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await drive.DeleteAsync(copy.GoogleAccountId, copy.DriveFileId, cancellationToken);

                copy.RemovedAt = clock.GetUtcNow();
                removed++;
            }
            catch (Exception exception) when (exception is DriveApiException or DriveRateLimitedException)
            {
                // Left unstamped so the next prune tries again. A row marked removed for a file that
                // is still there is space in the operator's pool that nothing will ever reclaim —
                // which is the same way round the migration's sweeper gets it.
                logger.LogWarning(
                    exception,
                    "Could not delete catalogue snapshot copy {DriveFileId} from account {AccountId}.",
                    copy.DriveFileId,
                    copy.GoogleAccountId);
            }
        }

        if (removed > 0) await db.SaveChangesAsync(cancellationToken);

        return removed;
    }

    /// <summary>
    /// The snapshot to write now: one somebody is waiting on, or a new one because the schedule has
    /// come round.
    ///
    /// <para>A <c>Running</c> row is taken ahead of a <c>Pending</c> one and is not a mistake — it is
    /// a run whose process died mid-write, and the right thing to do with it is to write it again
    /// rather than to leave a row that says «running» for ever.</para>
    /// </summary>
    private async Task<CatalogueSnapshot?> NextDueAsync(CancellationToken cancellationToken)
    {
        var live = await db.CatalogueSnapshots
            .Where(s => s.Status == CatalogueSnapshotStatus.Pending
                || s.Status == CatalogueSnapshotStatus.Running)
            .ToListAsync(cancellationToken);

        // In memory for the DateTimeOffset reason above. There is at most a handful of these rows:
        // everything else is finished.
        var waiting = live
            .OrderBy(s => s.Status == CatalogueSnapshotStatus.Running ? 0 : 1)
            .ThenBy(s => s.RequestedAt)
            .FirstOrDefault();

        if (waiting is not null) return waiting;

        var now = clock.GetUtcNow();

        if (await LastCompletedAtAsync(cancellationToken) is { } last
            && now - last < CatalogueSnapshot.Interval)
        {
            return null;
        }

        // Nothing to describe and nowhere to put it. A deployment with no account connected yet is
        // not a deployment that is failing to back up — it is one with no pool — and a failed row
        // per pass would be the panel shouting about a problem nobody has.
        if (!await db.GoogleAccounts.AnyAsync(cancellationToken)) return null;

        var scheduled = new CatalogueSnapshot
        {
            Id = Guid.NewGuid(),
            Name = CatalogueSnapshotFormat.NameFor(now),
            Status = CatalogueSnapshotStatus.Pending,
            ByHand = false,
            RequestedAt = now,
        };

        db.CatalogueSnapshots.Add(scheduled);
        await db.SaveChangesAsync(cancellationToken);

        return scheduled;
    }

    /// <summary>
    /// When the last good snapshot finished, or null if there has never been one.
    ///
    /// <para>The column and not a count: «is one due» is a question about the newest, and after a
    /// year there are three hundred and sixty-five rows that are not it. They are compared in
    /// memory for the reason every other date in this product is.</para>
    /// </summary>
    private async Task<DateTimeOffset?> LastCompletedAtAsync(CancellationToken cancellationToken)
    {
        var finished = await db.CatalogueSnapshots
            .Where(s => s.Status == CatalogueSnapshotStatus.Completed)
            .Select(s => new { s.FinishedAt, s.RequestedAt })
            .ToListAsync(cancellationToken);

        return finished.Count == 0
            ? null
            : finished.Max(s => s.FinishedAt ?? s.RequestedAt);
    }

    /// <summary>
    /// Streams the whole catalogue into every open session at once, and returns the copies that
    /// landed and were verified.
    ///
    /// <para>One pass over the database feeding every account, rather than a pass each: it is one
    /// read of a hundred thousand rows instead of two, and — the part that matters — the copies are
    /// then byte-identical, because they are literally the same bytes. Two passes would produce two
    /// snapshots taken a minute apart and both called the same thing.</para>
    /// </summary>
    private async Task<List<CatalogueSnapshotCopy>> WriteAsync(
        CatalogueSnapshot snapshot,
        DateTimeOffset takenAt,
        CancellationToken cancellationToken)
    {
        var targets = await OpenTargetsAsync(snapshot, cancellationToken);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var pending = new MemoryStream();

        var sent = 0L;

        // Sends whole chunks while the buffer is over-full, and on the last call sends whatever is
        // left — which is the only chunk allowed to be a partial size, and the only one that knows
        // how long the file turned out to be.
        //
        // The loop is «>» and not «>=» on purpose: draining to exactly empty would leave nothing for
        // the final chunk to carry, and with it no chunk on which to declare the total. The upload
        // would then sit waiting for a byte that never comes.
        async Task PushAsync(bool final)
        {
            while (pending.Length > _chunkSize)
            {
                await SendAsync(
                    targets,
                    pending.GetBuffer(),
                    _chunkSize,
                    sent,
                    UploadChunking.UnknownTotal,
                    cancellationToken);

                digest.AppendData(pending.GetBuffer(), 0, _chunkSize);
                sent += _chunkSize;

                Compact(pending, _chunkSize);
            }

            if (!final) return;

            var remaining = (int)pending.Length;

            await SendAsync(targets, pending.GetBuffer(), remaining, sent, sent + remaining, cancellationToken);

            digest.AppendData(pending.GetBuffer(), 0, remaining);
            sent += remaining;

            Compact(pending, remaining);
        }

        var counted = new SnapshotCounts();

        // leaveOpen, because the buffer outlives the compressor: disposing the GZipStream is what
        // writes the last deflate block and the trailer, and those bytes still have to be sent.
        using (var gzip = new GZipStream(pending, CompressionLevel.Optimal, leaveOpen: true))
        using (var lines = new CatalogueSnapshotLines(gzip))
        {
            await FillAsync(lines, counted, takenAt, PushAsync, cancellationToken);
        }

        await PushAsync(final: true);

        snapshot.TenantCount = counted.Tenants;
        snapshot.AccountCount = counted.Accounts;
        snapshot.FolderCount = counted.Folders;
        snapshot.FileCount = counted.Files;
        snapshot.EncryptionCount = counted.Encryptions;
        snapshot.SizeBytes = sent;

        return await VerifyAsync(snapshot, targets, sent, Convert.ToHexStringLower(digest.GetHashAndReset()), cancellationToken);
    }

    /// <summary>
    /// Every row that has to survive, in the order a person restoring would want to read them:
    /// what the file is, then the workspaces, then the accounts, then the tree, then the files, then
    /// the headers that open the encrypted ones.
    ///
    /// <para><b>Deleted files are in it.</b> A row in the trash is bytes still sitting in the pool
    /// with a deadline on them; a snapshot that dropped them would restore a catalogue that no
    /// longer knows those objects exist, and nothing would ever purge them. The <c>deletedAt</c> and
    /// <c>purgeAfter</c> fields carry the state so a restore puts them back in the trash rather than
    /// in front of the customer.</para>
    ///
    /// <para><b>Everything streams and nothing is tracked.</b> <c>AsAsyncEnumerable</c> keeps one
    /// row in flight rather than materialising a list, and <c>AsNoTracking</c> is what stops the
    /// change tracker turning the stream back into the whole catalogue in memory. That does hold a
    /// database connection open for the length of the upload, which is the price of not holding the
    /// catalogue instead — and this runs once a day, unattended, on the operator's own schedule.</para>
    /// </summary>
    private async Task FillAsync(
        CatalogueSnapshotLines lines,
        SnapshotCounts counted,
        DateTimeOffset takenAt,
        Func<bool, Task> push,
        CancellationToken cancellationToken)
    {
        lines.Header(takenAt);

        // Both of these are small and both are needed as lookups below, so they are read whole
        // rather than streamed. A deployment with so many workspaces that this is a problem has
        // bigger ones.
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(cancellationToken);

        // A projection and not the entity: see SnapshotAccountRow for why the refresh token must
        // never be loaded in the first place.
        var accounts = await db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new SnapshotAccountRow(
                a.Id, a.Label, a.Email, a.GoogleUserId, a.RootFolderId, a.Status))
            .ToListAsync(cancellationToken);

        var slugOf = tenants.ToDictionary(t => t.Id, t => t.Slug);
        var labelOf = accounts.ToDictionary(a => a.Id, a => a.Label);

        foreach (var tenant in tenants)
        {
            lines.Tenant(tenant);
            counted.Tenants++;
        }

        foreach (var account in accounts)
        {
            lines.Account(account);
            counted.Accounts++;
        }

        await push(false);

        // Ordered by id throughout. Not for correctness — nothing reads these in order — but so two
        // snapshots of an unchanged catalogue are the same file, which is what makes «what changed
        // between Tuesday and Friday» a diff rather than an investigation.
        await foreach (var folder in db.Folders
            .AsNoTracking()
            .OrderBy(f => f.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            lines.Folder(folder);
            counted.Folders++;

            await push(false);
        }

        await foreach (var file in db.StoredFiles
            .AsNoTracking()
            .OrderBy(f => f.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            // A tenant or an account that has no row is not a reason to drop the file: the file is
            // still in Drive and the id is still the truth about where. The dash is a placeholder a
            // reader can see, and the ids beside it are what a restore actually uses.
            lines.File(
                file,
                slugOf.GetValueOrDefault(file.TenantId, "—"),
                labelOf.GetValueOrDefault(file.GoogleAccountId, "—"));

            counted.Files++;

            // Asked after every row rather than every thousandth. The call is a length comparison
            // against the buffer and nothing else until there is genuinely a chunk to send, and a
            // counter that decided when to look would be a second thing to get wrong.
            await push(false);
        }

        await foreach (var header in db.FileEncryptions
            .AsNoTracking()
            .OrderBy(e => e.StoredFileId)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            lines.Encryption(header);
            counted.Encryptions++;

            await push(false);
        }

        lines.Footer(
            counted.Tenants,
            counted.Accounts,
            counted.Folders,
            counted.Files,
            counted.Encryptions);
    }

    /// <summary>
    /// Opens a resumable upload on every account that is going to get a copy.
    ///
    /// <para><b>Healthy accounts only.</b> A disconnected one cannot be written to at all, and a
    /// paused one is either being drained or deliberately withheld by the operator — putting the
    /// index of the whole product onto an account somebody is in the middle of emptying is the one
    /// place it is guaranteed not to stay.</para>
    ///
    /// <para><b>Most free space first</b>, the same ordering and the same treatment of an unknown
    /// quota as the upload selector, so the snapshot lands where the files are landing rather than
    /// in some corner of the pool with its own rules.</para>
    ///
    /// <para>An account that refuses to open a session is dropped and the rest go ahead. The whole
    /// reason for a second copy is that an account can be the thing that is broken.</para>
    /// </summary>
    private async Task<List<SnapshotTarget>> OpenTargetsAsync(
        CatalogueSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var candidates = await db.GoogleAccounts
            .AsNoTracking()
            .Where(a => a.Status == GoogleAccountStatus.Healthy)
            .Select(a => new { a.Id, a.Label, a.QuotaTotalBytes, a.QuotaUsedBytes })
            .ToListAsync(cancellationToken);

        var chosen = candidates
            .OrderByDescending(a => a.QuotaTotalBytes - a.QuotaUsedBytes)

            // Tie-broken on something stable, so a pool of identical accounts does not put each
            // night's snapshot somewhere else and leave the operator unable to say where they are.
            .ThenBy(a => a.Label, StringComparer.Ordinal)
            .ThenBy(a => a.Id)
            .Take(CatalogueSnapshot.Copies)
            .ToList();

        if (chosen.Count == 0)
        {
            throw new InvalidOperationException(
                "No connected account would take a catalogue snapshot. Every account in the pool is "
                + "disconnected or paused, so there is nowhere to write the index of what they are "
                + "holding.");
        }

        // How many were meant to get one, recorded before any of them is tried. Set here rather than
        // from the sessions that opened, because «wanted two and got one» is the sentence the screen
        // needs and counting the survivors would make it «wanted one and got one».
        snapshot.CopiesWanted = chosen.Count;

        var targets = new List<SnapshotTarget>();

        foreach (var account in chosen)
        {
            try
            {
                var folderId = await folders.CatalogueAsync(account.Id, cancellationToken);

                var session = await drive.BeginResumableUploadAsync(
                    account.Id,
                    new DriveUploadRequest(

                        // UnknownTotal: the length of a gzip stream is whatever the compressor makes
                        // of a hundred thousand rows, and the alternatives are to hold the whole
                        // snapshot in memory or to build it twice. See UploadChunking.UnknownTotal.
                        snapshot.Name,
                        CatalogueSnapshotFormat.MimeType,
                        UploadChunking.UnknownTotal,
                        folderId),
                    cancellationToken);

                targets.Add(new SnapshotTarget(account.Id, folderId, session.SessionUri));
            }
            catch (Exception exception) when (IsWorthRetrying(exception))
            {
                logger.LogWarning(
                    exception,
                    "Account {AccountId} would not open a session for catalogue snapshot {Name}.",
                    account.Id,
                    snapshot.Name);
            }
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                "Every account refused to open an upload session, so no catalogue snapshot was "
                + "written.");
        }

        return targets;
    }

    /// <summary>
    /// Writes one chunk into every session that is still alive, and drops the ones that fail.
    ///
    /// <para>A failure here is one account's problem, not the run's: the other copy is the entire
    /// point. The run only fails when there is nothing left to write to, and it says so rather than
    /// reporting a snapshot that exists nowhere.</para>
    /// </summary>
    private async Task SendAsync(
        List<SnapshotTarget> targets,
        byte[] buffer,
        int count,
        long offset,
        long totalSize,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            if (!target.Alive) continue;

            try
            {
                // A fresh reader per target over the same array. Nothing is copied — the sessions
                // are being handed the same bytes, which is what makes the copies identical.
                using var chunk = new MemoryStream(buffer, 0, count, writable: false);

                var outcome = await drive.WriteChunkAsync(
                    target.SessionUri, chunk, offset, count, totalSize, cancellationToken);

                if (outcome.Completed is { } landed) target.Completed = landed;
            }
            catch (Exception exception) when (IsWorthRetrying(exception))
            {
                target.Drop();

                logger.LogWarning(
                    exception,
                    "Account {AccountId} stopped taking the catalogue snapshot after {Offset} bytes.",
                    target.AccountId,
                    offset);
            }
        }

        if (targets.TrueForAll(t => !t.Alive))
        {
            throw new InvalidOperationException(
                $"Every account stopped taking the catalogue snapshot after {offset} bytes, so "
                + "nothing complete was written.");
        }
    }

    /// <summary>
    /// Asks Drive what it actually stored, and records only the copies it agrees about.
    ///
    /// <para><b>A missing checksum is accepted here and is refused by the migration</b>, and the
    /// difference is what happens next. The migration deletes somebody's only copy on the strength
    /// of its answer, so «I could not check» must never read as «I checked». Nothing is deleted on
    /// the strength of this one: the worst case is a snapshot that turns out to be unreadable, and
    /// refusing it outright would mean no snapshot at all — which is strictly worse than one with an
    /// unverified checksum. A wrong <i>length</i> is still refused, because that is truncation and
    /// truncation is the way this actually goes wrong.</para>
    /// </summary>
    private async Task<List<CatalogueSnapshotCopy>> VerifyAsync(
        CatalogueSnapshot snapshot,
        List<SnapshotTarget> targets,
        long expectedLength,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        var copies = new List<CatalogueSnapshotCopy>();
        var now = clock.GetUtcNow();

        foreach (var target in targets)
        {
            if (!target.Alive) continue;

            if (target.Completed is not { } landed)
            {
                logger.LogWarning(
                    "Account {AccountId} took every byte of {Name} and never completed the upload.",
                    target.AccountId,
                    snapshot.Name);

                continue;
            }

            try
            {
                var stored = await drive.GetFileAsync(target.AccountId, landed.FileId, cancellationToken);

                if (stored is null)
                {
                    logger.LogWarning(
                        "Account {AccountId} reported {Name} complete and then could not find it.",
                        target.AccountId,
                        snapshot.Name);

                    continue;
                }

                if (stored.SizeBytes != expectedLength)
                {
                    logger.LogWarning(
                        "The copy of {Name} on account {AccountId} is {Stored} bytes and {Expected} "
                        + "were sent, so it is not the whole snapshot.",
                        snapshot.Name,
                        target.AccountId,
                        stored.SizeBytes,
                        expectedLength);

                    continue;
                }

                if (stored.Md5Checksum is { Length: > 0 } actual
                    && !string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "The copy of {Name} on account {AccountId} is the right length and the wrong "
                        + "bytes.",
                        snapshot.Name,
                        target.AccountId);

                    continue;
                }

                copies.Add(new CatalogueSnapshotCopy
                {
                    Id = Guid.NewGuid(),
                    SnapshotId = snapshot.Id,
                    GoogleAccountId = target.AccountId,
                    DriveFileId = landed.FileId,
                    DriveFolderId = target.FolderId,
                    SizeBytes = expectedLength,
                    WrittenAt = now,
                });
            }
            catch (Exception exception) when (IsWorthRetrying(exception))
            {
                logger.LogWarning(
                    exception,
                    "Could not confirm the copy of {Name} on account {AccountId}.",
                    snapshot.Name,
                    target.AccountId);
            }
        }

        if (copies.Count == 0)
        {
            throw new InvalidOperationException(
                "Not one account ended up holding a snapshot this run could confirm, so nothing was "
                + "recorded — an unconfirmed backup is worse than a missing one, because it is the "
                + "one nobody checks.");
        }

        return copies;
    }

    private async Task FailAsync(
        CatalogueSnapshot snapshot,
        Exception exception,
        CancellationToken cancellationToken)
    {
        snapshot.FailureReason = Trimmed(exception.Message);

        if (snapshot.Attempts >= CatalogueSnapshot.MaxAttempts)
        {
            snapshot.Status = CatalogueSnapshotStatus.Failed;
            snapshot.FinishedAt = clock.GetUtcNow();

            // An error rather than a warning, and the only one in this class. A catalogue backup
            // that has stopped working is invisible until the day it is needed, which is the day
            // nobody can do anything about it.
            logger.LogError(
                exception,
                "Gave up writing catalogue snapshot {Name} after {Attempts} attempts.",
                snapshot.Name,
                snapshot.Attempts);
        }
        else
        {
            // Back in the queue. A rate limit or a token that expired mid-write is worth another go,
            // and the next pass is a minute away rather than a day.
            snapshot.Status = CatalogueSnapshotStatus.Pending;

            logger.LogWarning(
                exception,
                "Attempt {Attempt} at catalogue snapshot {Name} failed.",
                snapshot.Attempts,
                snapshot.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Everything a snapshot is allowed to fail on without taking the worker down with it.
    ///
    /// <para>Deliberately not a bare <c>catch</c>: a <c>NullReferenceException</c> in this class is a
    /// bug in this class, and swallowing it onto a row nobody reads is how it survives to the next
    /// release.</para>
    /// </summary>
    private static bool IsWorthRetrying(Exception exception) => exception
        is DriveApiException
        or DriveRateLimitedException
        or DriveUploadSessionExpiredException
        or IOException
        or InvalidOperationException;

    /// <summary>
    /// Drops the bytes that have been sent and slides what is left to the front.
    ///
    /// <para>The same move the fetcher makes after flushing a chunk of ciphertext, and for the same
    /// reason: the buffer is the thing that must not grow with the file.</para>
    /// </summary>
    private static void Compact(MemoryStream pending, int consumed)
    {
        var remaining = (int)pending.Length - consumed;

        if (remaining > 0)
        {
            Buffer.BlockCopy(pending.GetBuffer(), consumed, pending.GetBuffer(), 0, remaining);
        }

        pending.SetLength(remaining);
        pending.Position = remaining;
    }

    private static string Trimmed(string message) =>
        message.Length <= CatalogueSnapshot.MaxFailureReasonLength
            ? message
            : message[..CatalogueSnapshot.MaxFailureReasonLength];

    /// <summary>What went into the file, counted as it went rather than queried afterwards.</summary>
    private sealed class SnapshotCounts
    {
        public int Tenants { get; set; }

        public int Accounts { get; set; }

        public int Folders { get; set; }

        public int Files { get; set; }

        public int Encryptions { get; set; }
    }

    /// <summary>
    /// One account's copy while it is being written.
    ///
    /// <para><see cref="Alive"/> going false is not the end of the run — it is this account being
    /// out of it. Which is the case the second copy exists for.</para>
    /// </summary>
    private sealed class SnapshotTarget(Guid accountId, string folderId, Uri sessionUri)
    {
        public Guid AccountId { get; } = accountId;

        public string FolderId { get; } = folderId;

        public Uri SessionUri { get; } = sessionUri;

        public bool Alive { get; private set; } = true;

        /// <summary>What Drive said when the last chunk finished the file. Null until then.</summary>
        public DriveFileMetadata? Completed { get; set; }

        /// <summary>This account is out of this run. What went wrong is logged where it happened.</summary>
        public void Drop() => Alive = false;
    }
}
