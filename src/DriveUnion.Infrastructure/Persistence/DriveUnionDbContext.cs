using DriveUnion.Core.Api;
using DriveUnion.Core.Metering;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Settings;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Telegram;
using DriveUnion.Core.Tenancy;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence;

public sealed class DriveUnionDbContext(DbContextOptions<DriveUnionDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<GoogleAccount> GoogleAccounts => Set<GoogleAccount>();

    /// <summary>
    /// The operator's Google OAuth clients, with their secrets encrypted by the same key ring as the
    /// refresh tokens below.
    ///
    /// They were a JSON file in <c>App_Data</c> until a redeploy deleted it and took the whole pool
    /// with it: the refresh tokens survived — they are rows — but a refresh has to present the client
    /// id and secret alongside the token, and those were gone. A table is the only store here that is
    /// known to survive a deploy, which is the same argument <see cref="DataProtectionKeys"/> makes.
    /// </summary>
    public DbSet<GoogleOAuthClient> GoogleOAuthClients => Set<GoogleOAuthClient>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<Folder> Folders => Set<Folder>();

    /// <summary>How to open an encrypted file — and nothing that opens one. See <see cref="FileEncryption"/>.</summary>
    public DbSet<FileEncryption> FileEncryptions => Set<FileEncryption>();

    public DbSet<TenantUsageDay> TenantUsageDays => Set<TenantUsageDay>();

    /// <summary>Draining one pool account into another. See <see cref="AccountMigration"/>.</summary>
    public DbSet<AccountMigration> AccountMigrations => Set<AccountMigration>();

    /// <summary>One file's move, and where its old copy still is.</summary>
    public DbSet<FileRelocation> FileRelocations => Set<FileRelocation>();

    /// <summary>
    /// Deletions too big to finish inside a request. See <see cref="DeletionJob"/> for why the
    /// visible half of a delete happens immediately and the Drive moves are owed.
    /// </summary>
    public DbSet<DeletionJob> DeletionJobs => Set<DeletionJob>();

    /// <summary>The customer's API keys. Hashes, never secrets — see <see cref="ApiToken"/>.</summary>
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    /// <summary>S3 access keys. Encrypted and not hashed — see <see cref="S3Credential"/>.</summary>
    public DbSet<S3Credential> S3Credentials => Set<S3Credential>();

    /// <summary>S3 multipart uploads in flight, and the parts staged for them.</summary>
    public DbSet<S3MultipartUpload> S3MultipartUploads => Set<S3MultipartUpload>();

    public DbSet<S3UploadPart> S3UploadParts => Set<S3UploadPart>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<FileTag> FileTags => Set<FileTag>();

    /// <summary>
    /// The operator's own knobs. One row, seeded by the migration that made the table.
    ///
    /// It is a table for the reason the Google OAuth client is now one: a setting an operator sets
    /// by pressing something has to still be true after a deploy, and this product has already lost
    /// one that lived in a file inside the container.
    /// </summary>
    public DbSet<OperatorSettings> OperatorSettings => Set<OperatorSettings>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    /// <summary>Files the customer asked the server to go and get. See <see cref="RemoteFetch"/>.</summary>
    public DbSet<RemoteFetch> RemoteFetches => Set<RemoteFetch>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();

    public DbSet<ShareLinkKey> ShareLinkKeys => Set<ShareLinkKey>();

    /// <summary>Complaints about public links. See <see cref="AbuseReport"/> for what they prevent.</summary>
    public DbSet<AbuseReport> AbuseReports => Set<AbuseReport>();
    public DbSet<DownloadEvent> DownloadEvents => Set<DownloadEvent>();

    /// <summary>
    /// The operator's catalogue of tiers. It carries no <c>TenantId</c>, like
    /// <see cref="GoogleAccounts"/> and <see cref="TelegramBotSettings"/>, and <b>nothing on any
    /// enforcement path joins to it</b> — assigning a plan copies its numbers onto the tenant's row.
    /// </summary>
    public DbSet<Plan> Plans => Set<Plan>();

    /// <summary>
    /// Who moved a tenant's ceiling, when, and why. The answer to «چرا سهمیه‌ام عوض شد», which a
    /// template edit could never have produced per tenant.
    /// </summary>
    public DbSet<TenantQuotaChange> TenantQuotaChanges => Set<TenantQuotaChange>();

    /// <summary>The operator's bot. One row, seeded empty by the migration that created this table.</summary>
    public DbSet<TelegramBotSettings> TelegramBotSettings => Set<TelegramBotSettings>();

    public DbSet<TelegramAccount> TelegramAccounts => Set<TelegramAccount>();

    public DbSet<TelegramLinkToken> TelegramLinkTokens => Set<TelegramLinkToken>();

    /// <summary>
    /// What the bot owes a chat. Its <c>TenantId</c> is not nullable and is the drainer's only source
    /// of tenant identity — the drainer runs with no request, no cookie and no principal.
    /// </summary>
    public DbSet<TelegramOutbox> TelegramOutbox => Set<TelegramOutbox>();

    /// <summary>The <c>file_id</c> cache, keyed on the file and the bot that minted it.</summary>
    public DbSet<TelegramFileId> TelegramFileIds => Set<TelegramFileId>();

    /// <summary>One row per update handled, which is why a webhook retry does not upload a file twice.</summary>
    public DbSet<TelegramUpdateSeen> TelegramUpdatesSeen => Set<TelegramUpdateSeen>();

    /// <summary>
    /// Data Protection keys live here rather than on disk. Keys in a container filesystem are lost
    /// on the first redeploy, and every encrypted Google token in the table above becomes garbage
    /// at the same moment — presenting as both accounts having disconnected themselves.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ────────────────────────────────────────────────────────────────────────────────────────
        // There are deliberately NO global query filters in this model, and adding one is a
        // breaking change even though it compiles.
        //
        // /d/{slug} is an anonymous request. A tenant filter fed by the signed-in user resolves it
        // to an empty tenant, and every public link in the product returns "not found" while its
        // row sits plainly in the table — a failure that reads like a routing bug and is invisible
        // to every test that signs in first. A sibling project shipped exactly this and it broke
        // four unrelated things silently in one day.
        //
        // Scoping is therefore an explicit tenantId argument on every query that needs it: a
        // forgotten scope becomes a compile error instead of an empty result set.
        // ────────────────────────────────────────────────────────────────────────────────────────

        builder.Entity<Tenant>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Slug).HasMaxLength(64);
            e.HasIndex(t => t.Slug).IsUnique();

            // Why an operator stopped this workspace's public links. AbuseReports trims to the same
            // constant before writing; the column carries it too so the bound is one fact in both
            // places rather than a service-layer habit an unrelated caller can skip.
            e.Property(t => t.PublicSuspendedReason)
                .HasMaxLength(Tenant.MaxSuspensionReasonLength);

            // The four effective limits carry the smallest seeded tier as their column default, so a
            // row inserted by something that has not been taught about plans — the sign-up path, a
            // support insert, a fixture — is capped rather than either uncapped or capped at zero.
            // Zero would refuse every upload in the product; unlimited is the shape §3 refuses.
            //
            // ValueGeneratedNever beside each: HasDefaultValue alone makes EF omit the property from
            // an INSERT whenever it holds the CLR type's default, so an explicit zero would silently
            // become the tier's number. The DDL default stays for the inserts EF does not write; the
            // value the code holds is always the value that is sent.
            e.Property(t => t.StorageQuotaBytes)
                .HasDefaultValue(PlanCatalogue.Default.StorageBytes)
                .ValueGeneratedNever();
            e.Property(t => t.MaxFileBytes)
                .HasDefaultValue(PlanCatalogue.Default.MaxFileBytes)
                .ValueGeneratedNever();
            e.Property(t => t.MonthlyEgressBytes)
                .HasDefaultValue(PlanCatalogue.Default.MonthlyEgressBytes)
                .ValueGeneratedNever();
            e.Property(t => t.MaxMembers)
                .HasDefaultValue(PlanCatalogue.Default.MaxMembers)
                .ValueGeneratedNever();

            // No navigation and no cascade. The plan is a label on the row, not a source of numbers,
            // and a foreign key with a delete rule would be the first thing a reader mistook for a
            // join the enforcement path makes. Retirement, not deletion, is how a plan goes away.
            e.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(t => t.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Plan>(e =>
        {
            e.Property(p => p.Code).HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(120);

            // The code is what a configuration file and an operator name a tier by, so two rows
            // sharing one would make Plans:DefaultPlanCode depend on row order.
            e.HasIndex(p => p.Code).IsUnique();

            // The catalogue exists from the first migration that knows about plans, because the
            // alternative — create it on first use — means every read has to cope with its absence,
            // and Plans:DefaultPlanCode has to name something on the day the product boots.
            //
            // Every figure in PlanCatalogue is a placeholder: §15.2 leaves the names and all four
            // numbers per tier to the owner, and the operator's screen says so in words.
            e.HasData(PlanCatalogue.Seed());
        });

        builder.Entity<TenantQuotaChange>(e =>
        {
            e.Property(c => c.PlanCodeBefore).HasMaxLength(32);
            e.Property(c => c.PlanCodeAfter).HasMaxLength(32);
            e.Property(c => c.Reason).HasMaxLength(512);

            // The only query over this table: one tenant's history, on the operator's page for that
            // tenant. It is never read across tenants, because it is not an audit log.
            e.HasIndex(c => new { c.TenantId, c.ChangedAt });

            // Cascade, unlike the plan reference above: a tenant's quota history is about that tenant
            // and outlives nothing.
            e.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<GoogleAccount>(e =>
        {
            e.Property(a => a.Email).HasMaxLength(320);
            e.Property(a => a.Label).HasMaxLength(16);
            e.Property(a => a.RootFolderId).HasMaxLength(256);
            e.Property(a => a.GoogleUserId).HasMaxLength(64);

            // Google's client ids run to about 75 characters today. 128 is room for that plus a
            // format change, and a cap rather than unbounded text because this column is compared
            // against on every refresh.
            e.Property(a => a.OAuthClientId).HasMaxLength(128);

            // Google's own error text, kept verbatim. The same width UploadSession.FailureReason
            // uses, and for the same reason: long enough to hold the sentence, short enough that a
            // pathological body cannot become the largest thing in the table.
            e.Property(a => a.LastFailureReason).HasMaxLength(1024);

            // The address stays unique because two rows spelled identically are still a mistake,
            // and because it is what the operator reads. It is not what identity is decided on.
            e.HasIndex(a => a.Email).IsUnique();

            // Filtered, so the rows written before this column existed do not collide with each
            // other on being equally unknown. Two accounts that report the same permissionId are
            // one account, and this is what stops the pool counting its capacity twice.
            //
            // The predicate is written in the SQL both providers spell the same way: Postgres runs
            // it in production, SQLite runs it in the tests, and a filter with only one of them in
            // mind is a constraint that exists in exactly one of the two places it matters.
            e.HasIndex(a => a.GoogleUserId)
                .IsUnique()
                .HasFilter("\"GoogleUserId\" IS NOT NULL");
        });

        builder.Entity<GoogleOAuthClient>(e =>
        {
            e.Property(c => c.Label).HasMaxLength(16);
            e.Property(c => c.ClientId).HasMaxLength(128);
            e.Property(c => c.RedirectUri).HasMaxLength(2048);

            // Data Protection ciphertext is base64url and grows with the plaintext; a Google client
            // secret is short, and this is the same allowance the Telegram bot token gets.
            e.Property(c => c.ClientSecretProtected).HasMaxLength(2048);

            // Two rows holding the same client id would be two secrets for one credential, and which
            // one a refresh found would depend on row order — which is precisely the kind of answer
            // this table exists to stop being a coin toss. GoogleAccount.OAuthClientId names this
            // value, so it has to be the key an account is resolved by.
            e.HasIndex(c => c.ClientId).IsUnique();

            // No foreign key from GoogleAccounts to here, deliberately. An account may be connected
            // under the client a deployment supplies from its environment, which has no row at all,
            // and a constraint that could not express that would have to be satisfied by inventing
            // one. The dependency is enforced where it can be explained — GoogleOAuthClientStore
            // refuses to remove a client accounts still name, in a sentence.
        });

        builder.Entity<StoredFile>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(512);
            e.Property(f => f.MimeType).HasMaxLength(255);
            e.Property(f => f.DriveFileId).HasMaxLength(256);
            e.Property(f => f.DriveFolderId).HasMaxLength(256);
            e.Property(f => f.RestoreFolderId).HasMaxLength(256);
            e.HasIndex(f => new { f.TenantId, f.DeletedAt });
            e.HasIndex(f => new { f.GoogleAccountId, f.DriveFileId });

            // Browsing a folder is the panel's most-run query after the list itself.
            e.HasIndex(f => new { f.TenantId, f.FolderId, f.DeletedAt });

            // The sweeper's only query: what is due, oldest first, across every tenant. Filtered so
            // the index holds the trash rather than the whole catalogue — live files are the vast
            // majority and none of them has a deadline.
            e.HasIndex(f => f.PurgeAfter).HasFilter("\"PurgeAfter\" IS NOT NULL");

            // The deletion worker's only query: what does this job still owe. Filtered for the same
            // reason as the one above and to the same shape — the column is null on every live file
            // and on every file whose move has landed, which between them is the whole table.
            //
            // The predicate is spelled the way both providers spell it: Postgres runs it in
            // production and SQLite runs it in the tests, and a filter written for one of them is a
            // constraint that exists in exactly one of the two places it matters.
            e.HasIndex(f => f.PendingDeletionJobId).HasFilter("\"PendingDeletionJobId\" IS NOT NULL");
        });

        builder.Entity<Folder>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(Folder.MaxNameLength);

            // Listing one folder's children, and listing every folder in a workspace for the
            // breadcrumb and the «move to…» list. Both are this index.
            e.HasIndex(f => new { f.TenantId, f.ParentFolderId });

            // Self-referencing and Restrict, so a parent cannot be deleted while it still has
            // children. DeleteAsync refuses that in a sentence long before the database would, and
            // this is what stands behind the sentence: a cascade here would silently take a subtree
            // that the screen had just told the customer it would not touch.
            e.HasOne<Folder>()
                .WithMany()
                .HasForeignKey(f => f.ParentFolderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<S3MultipartUpload>(e =>
        {
            e.Property(u => u.Key).HasMaxLength(1024);
            e.Property(u => u.Name).HasMaxLength(512);
            e.Property(u => u.MimeType).HasMaxLength(255);
            e.HasIndex(u => u.TenantId);
        });

        builder.Entity<S3UploadPart>(e =>
        {
            // The pair is the identity: S3 lets a client re-upload a part number, and two rows for
            // one number would assemble the object twice over.
            e.HasKey(p => new { p.UploadId, p.PartNumber });
            e.Property(p => p.ETag).HasMaxLength(64);

            e.HasOne<S3MultipartUpload>()
                .WithMany()
                .HasForeignKey(p => p.UploadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<S3Credential>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(S3Credential.MaxNameLength);
            e.Property(c => c.AccessKeyId).HasMaxLength(S3Credential.AccessKeyIdLength);

            // The lookup every signed request makes, and unique: an access key id is the name a
            // client is configured with, and two rows answering to one would make which workspace
            // a request reached a matter of row order.
            e.HasIndex(c => c.AccessKeyId).IsUnique();
            e.HasIndex(c => c.TenantId);
        });

        builder.Entity<ApiToken>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(ApiToken.MaxNameLength);
            e.Property(t => t.Prefix).HasMaxLength(ApiToken.PrefixLength);

            // Base64 of a SHA-256 is 44 characters. Fixed rather than generous, so a row holding
            // anything other than a hash of that algorithm does not fit.
            e.Property(t => t.SecretHash).HasMaxLength(64);

            // The lookup every authenticated request makes. Not unique: eight base64url characters
            // across a whole deployment will collide eventually, and the hash is what decides —
            // a unique index here would turn that collision into a refusal to mint.
            e.HasIndex(t => t.Prefix);

            // …and the panel's list.
            e.HasIndex(t => t.TenantId);
        });

        builder.Entity<TenantUsageDay>(e =>
        {
            // The pair is the identity, and there is no surrogate key: one row per workspace per
            // day is the whole shape, and a Guid id would be a column nothing ever reads.
            e.HasKey(u => new { u.TenantId, u.Day });

            // Every read is «this workspace, this range of days», which the key already serves. The
            // one that is not is the operator's «every workspace this month», and that is this.
            e.HasIndex(u => u.Day);
        });

        builder.Entity<FileEncryption>(e =>
        {
            // The file is the key: one encryption row per file, or none. A surrogate id would be a
            // column nothing ever reads and a second way for two rows to claim one file.
            e.HasKey(x => x.StoredFileId);

            e.Property(x => x.NoncePrefix).HasMaxLength(FileEncryption.MaxFieldLength);
            e.Property(x => x.KdfSalt).HasMaxLength(FileEncryption.MaxFieldLength);
            e.Property(x => x.WrappedKey).HasMaxLength(FileEncryption.MaxFieldLength);

            // «Which of my files are encrypted», which every listing asks.
            e.HasIndex(x => x.TenantId);

            // Purging a file takes its header with it. Keeping one would be keeping a wrapped key
            // for bytes that no longer exist — useless, and one more thing in a backup.
            e.HasOne<StoredFile>()
                .WithMany()
                .HasForeignKey(x => x.StoredFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(Tag.MaxNameLength);
            e.HasIndex(t => t.TenantId);
        });

        builder.Entity<FileTag>(e =>
        {
            // The pair is the identity. A file carrying a tag twice is a row the reader cannot see
            // and a count that is wrong, and TagStore's «skip the ones that already have it» is the
            // sentence in front of this rather than a substitute for it.
            e.HasKey(ft => new { ft.StoredFileId, ft.TagId });

            // «Everything tagged X», which is the whole point of the feature.
            e.HasIndex(ft => new { ft.TenantId, ft.TagId });

            // Retiring a tag takes it off everything, and deleting a file for good takes its labels
            // with it. Neither is a decision a customer would want to make twice.
            e.HasOne<Tag>()
                .WithMany()
                .HasForeignKey(ft => ft.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<StoredFile>()
                .WithMany()
                .HasForeignKey(ft => ft.StoredFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OperatorSettings>(e =>
        {
            // Not an identity column. There is one row and its id is a constant the code names, so a
            // sequence here would be a generator nobody draws from — and one that does not know the
            // seeded 1 was taken, which is exactly how a second row eventually appears.
            e.Property(s => s.Id).ValueGeneratedNever();

            // One row, and the migration puts it there. Creating it on first use would mean every
            // reader coping with its absence, which is how a default ends up written in four places.
            e.HasData(new OperatorSettings
            {
                Id = Core.Settings.OperatorSettings.SingletonId,
                TrashRetentionDays = Core.Settings.OperatorSettings.DefaultTrashRetentionDays,
            });
        });

        builder.Entity<UploadSession>(e =>
        {
            e.Property(u => u.FileName).HasMaxLength(512);
            e.Property(u => u.MimeType).HasMaxLength(255);
            e.Property(u => u.DriveResumableUri).HasMaxLength(2048);
            e.Property(u => u.FailureReason).HasMaxLength(1024);
            e.HasIndex(u => new { u.TenantId, u.Status });
        });

        builder.Entity<ShareLink>(e =>
        {
            e.Property(l => l.Slug).HasMaxLength(32);
            e.Property(l => l.Note).HasMaxLength(ShareLink.MaxNoteLength);
            // The public route's only lookup, and the guard against a slug collision becoming two
            // links that resolve to different files depending on row order.
            e.HasIndex(l => l.Slug).IsUnique();
            e.HasIndex(l => new { l.TenantId, l.CreatedAt });
            e.HasOne<StoredFile>()
                .WithMany()
                .HasForeignKey(l => l.StoredFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RemoteFetch>(e =>
        {
            e.Property(f => f.Url).HasMaxLength(RemoteSource.MaxUrlLength);
            e.Property(f => f.FileName).HasMaxLength(RemoteFetch.MaxFileNameLength);
            e.Property(f => f.FailureReason).HasMaxLength(RemoteFetch.MaxFailureReasonLength);

            // The worker's question — what is queued — and the screen's, which is this workspace's
            // fetches. Status first: nearly every row is finished and is skipped on it alone.
            e.HasIndex(f => f.Status);
            e.HasIndex(f => new { f.TenantId, f.CreatedAt });

            // No foreign key to Tenant, and that is not an omission — UploadSession, which this row
            // sits directly beside, deliberately has none either.
            //
            // TenantStorageMeter reserves quota with ExecuteUpdate and then detaches the Tenant it
            // went round, or the next SaveChanges writes a stale counter back over the reservation.
            // Detaching a principal detaches its tracked dependents with it, so a fetch row related
            // to that tenant by a real FK is silently detached in the middle of its own job — every
            // write after the reserve is a no-op, the file lands, and the row still says «running».
            // Which is exactly what a foreign key here did before it was taken out again.
        });

        builder.Entity<DeletionJob>(e =>
        {
            e.Property(j => j.FolderName).HasMaxLength(Folder.MaxNameLength);
            e.Property(j => j.FailureReason).HasMaxLength(DeletionJob.MaxFailureReasonLength);

            // The worker's question — what is still owed — and the screen's, which is this
            // workspace's clean-ups. Status first: every row is Completed within minutes of being
            // made and is skipped on it alone.
            e.HasIndex(j => j.Status);
            e.HasIndex(j => new { j.TenantId, j.CreatedAt });

            // No foreign key to Tenant, and that is not an omission — UploadSession and RemoteFetch
            // deliberately have none either, and the reasoning is written out on RemoteFetch below.
            // The short version: TenantStorageMeter detaches the Tenant it reserved against, and
            // detaching a principal detaches its tracked dependents, so a job row tied to that tenant
            // by a real FK stops being written halfway through its own work.
        });

        builder.Entity<AccountMigration>(e =>
        {
            e.Property(m => m.FailureReason).HasMaxLength(AccountMigration.MaxFailureReasonLength);

            // «Is one already running for this source» — asked before every start, because two
            // drains of one account would fight over the same files.
            e.HasIndex(m => new { m.SourceAccountId, m.Status });

            // No foreign key to GoogleAccount, and that is deliberate: disconnecting an account the
            // operator has finished draining must not take the record of the drain with it. What it
            // moved and what it failed on is exactly the history somebody needs after the account
            // is gone.
        });

        builder.Entity<FileRelocation>(e =>
        {
            e.Property(r => r.SourceDriveFileId).HasMaxLength(512);
            e.Property(r => r.TargetDriveFileId).HasMaxLength(512);
            e.Property(r => r.FailureReason).HasMaxLength(AccountMigration.MaxFailureReasonLength);

            // The worker's own question: what has this migration already dealt with.
            e.HasIndex(r => new { r.MigrationId, r.Status });

            // The sweeper's: which source copies are old enough to delete. Status first, because
            // the overwhelming majority of rows are SourceRemoved and are skipped on it alone.
            e.HasIndex(r => new { r.Status, r.MovedAt });

            e.HasOne<AccountMigration>()
                .WithMany()
                .HasForeignKey(r => r.MigrationId)
                .OnDelete(DeleteBehavior.Cascade);

            // No cascade from StoredFile. A file purged while its old copy is still standing would
            // otherwise take the only record of where that copy is, and the bytes would sit in the
            // operator's pool for ever with nothing left that knows about them.
        });

        builder.Entity<AbuseReport>(e =>
        {
            e.Property(r => r.Note).HasMaxLength(AbuseReport.MaxNoteLength);
            e.Property(r => r.ReporterEmail).HasMaxLength(AbuseReport.MaxEmailLength);
            e.Property(r => r.ReporterIpHash).HasMaxLength(128);
            e.Property(r => r.Resolution).HasMaxLength(AbuseReport.MaxResolutionLength);

            // The queue's own question, and the badge's.
            e.HasIndex(r => r.Status);

            // «Has this link been reported already» on the way in, and «how many for this
            // workspace» on the way out — the number that decides whether one file is a mistake.
            e.HasIndex(r => new { r.ShareLinkId, r.Status });
            e.HasIndex(r => new { r.TenantId, r.Status });

            // No foreign key to ShareLink or Tenant, and neither is an oversight.
            //
            // The report has to outlive both. A link revoked and purged, a workspace closed — that
            // is precisely when somebody asks what was reported and what was done about it, and a
            // cascade would have taken the answer with the thing it was about. It is also the same
            // reason AccountMigration keeps no key to GoogleAccount.
            //
            // And a key to Tenant would be actively harmful here: TenantStorageMeter detaches the
            // tenant it reserves against, which detaches its tracked dependents with it. A row
            // silently detached mid-request is every write after that point becoming a no-op.
        });

        builder.Entity<ShareLinkKey>(e =>
        {
            // The link is the key, for the same reason the file is FileEncryption's: one re-wrap per
            // link or none, and no second way for two rows to claim one link.
            e.HasKey(x => x.ShareLinkId);

            // The same ceiling as the file's own fields — it is the same wrapped key, wrapped again.
            e.Property(x => x.KdfSalt).HasMaxLength(FileEncryption.MaxFieldLength);
            e.Property(x => x.WrappedKey).HasMaxLength(FileEncryption.MaxFieldLength);

            // Revoking a link is what takes the recipient's copy of the key away, so the row must go
            // when the link does. This is the half of «revoke» that makes it mean anything for an
            // encrypted file: the bytes stay reachable to whoever kept a copy, and the key does not.
            e.HasOne<ShareLink>()
                .WithMany()
                .HasForeignKey(x => x.ShareLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DownloadEvent>(e =>
        {
            e.Property(d => d.IpHash).HasMaxLength(128);
            e.Property(d => d.UserAgent).HasMaxLength(512);
            e.HasIndex(d => new { d.ShareLinkId, d.OccurredAt });
            e.HasOne<ShareLink>()
                .WithMany()
                .HasForeignKey(d => d.ShareLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelegramBotSettings>(e =>
        {
            // No identity column: there is exactly one row and its key is written by the seed below,
            // so a second row would have to be inserted deliberately rather than by a default.
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.BotTokenProtected).HasMaxLength(2048);

            // Telegram documents a bot username as 5–32 characters ending in "bot".
            e.Property(s => s.BotUsername).HasMaxLength(32);

            // The registration, which only exists once a transport does. All three are encrypted
            // with the same key ring as the token: the secret is what authenticates every inbound
            // update, and the path segment is what keeps the route out of a scanner's log.
            e.Property(s => s.WebhookPathSegmentProtected).HasMaxLength(2048);
            e.Property(s => s.WebhookSecretProtected).HasMaxLength(2048);

            // The row exists from the first migration, empty. The alternative — create it on first
            // save — means every read has to cope with its absence, and the screen that reads it is
            // the one an operator opens precisely because nothing is configured yet.
            e.HasData(new Core.Telegram.TelegramBotSettings
            {
                Id = Core.Telegram.TelegramBotSettings.SingletonId,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
        });

        builder.Entity<TelegramAccount>(e =>
        {
            e.Property(a => a.Username).HasMaxLength(32);
            e.Property(a => a.DisplayName).HasMaxLength(256);
            e.Property(a => a.LanguageCode).HasMaxLength(16);

            // Both directions are unique, which is what makes "one Telegram account per panel user,
            // and one panel user per Telegram account" a property of the database rather than of
            // whichever code path happens to check first.
            e.HasIndex(a => a.AppUserId).IsUnique();
            e.HasIndex(a => a.TelegramUserId).IsUnique();

            // Cascade as a backstop for a direct SQL delete of the user. It is only a backstop: a
            // cascade is silent, and §6.3 of the design is about the customer being told, which is
            // what ITelegramLinkService.UnlinkAsync exists to do.
            e.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(a => a.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelegramLinkToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.ConfirmationCodeHash).HasMaxLength(64);

            // The same widths as the columns these are copied into when the binding is written.
            e.Property(t => t.PresentedUsername).HasMaxLength(32);
            e.Property(t => t.PresentedDisplayName).HasMaxLength(256);
            e.Property(t => t.PresentedLanguageCode).HasMaxLength(16);

            // The bot's leg looks a token up by its hash and by nothing else, so this is the index
            // that has to exist. Unique because two rows sharing a hash would be two rows sharing a
            // token, and the conditional consumption could then bind twice.
            e.HasIndex(t => t.TokenHash).IsUnique();

            // The panel's leg looks up "the pending request for this user".
            e.HasIndex(t => new { t.AppUserId, t.ConsumedAt });

            e.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(t => t.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelegramOutbox>(e =>
        {
            e.Property(o => o.ErrorCode).HasMaxLength(64);
            e.Property(o => o.ErrorDetail).HasMaxLength(1024);

            // The drainer's own lookup: everything still owed, in creation order. It is not filtered
            // by NextAttemptAt in SQL — SQLite stores a DateTimeOffset as text and will not compare
            // one, the same reason PublicLinkReader and TelegramLinkService both give — so the
            // scheduling columns are judged in memory over a queue that is bounded per tenant by
            // design.
            e.HasIndex(o => new { o.Status, o.CreatedAt });

            // Round-robin fairness reads "when was this tenant last served", which is a per-tenant
            // maximum over sent rows.
            e.HasIndex(o => new { o.TenantId, o.Status });

            // The queue holds a file id but deliberately no foreign key to StoredFile. A customer who
            // deletes a file with a delivery queued must not have the delete refused or the row
            // silently cascaded out from under the drainer: the drainer re-resolves the file through
            // the tenant-scoped catalogue and reports it as unavailable, which is the same answer a
            // crafted callback gets.
        });

        builder.Entity<TelegramFileId>(e =>
        {
            // Keyed on the bot as well as the file, because a file_id is unique per bot and cannot be
            // transferred to another one. Pointing the panel at a different token must produce a
            // cache miss, never a wrong send.
            e.HasKey(f => new { f.StoredFileId, f.BotUserId });

            e.Property(f => f.FileId).HasMaxLength(256);
            e.Property(f => f.FileUniqueId).HasMaxLength(128);

            e.HasOne<StoredFile>()
                .WithMany()
                .HasForeignKey(f => f.StoredFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelegramUpdateSeen>(e =>
        {
            // Telegram's own update_id is the key. No surrogate: the whole value of this table is
            // that a second insert of the same id fails, and a surrogate key would make it succeed.
            e.HasKey(u => u.UpdateId);
            e.Property(u => u.UpdateId).ValueGeneratedNever();
        });
    }
}
