using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Telegram;
using DriveUnion.Core.Tenancy;
using DriveUnion.Core.Uploads;
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
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<DownloadEvent> DownloadEvents => Set<DownloadEvent>();

    /// <summary>The operator's bot. One row, seeded empty by the migration that created this table.</summary>
    public DbSet<TelegramBotSettings> TelegramBotSettings => Set<TelegramBotSettings>();

    public DbSet<TelegramAccount> TelegramAccounts => Set<TelegramAccount>();

    public DbSet<TelegramLinkToken> TelegramLinkTokens => Set<TelegramLinkToken>();

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
        });

        builder.Entity<GoogleAccount>(e =>
        {
            e.Property(a => a.Email).HasMaxLength(320);
            e.Property(a => a.Label).HasMaxLength(16);
            e.Property(a => a.RootFolderId).HasMaxLength(256);
            e.HasIndex(a => a.Email).IsUnique();
        });

        builder.Entity<StoredFile>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(512);
            e.Property(f => f.MimeType).HasMaxLength(255);
            e.Property(f => f.DriveFileId).HasMaxLength(256);
            e.HasIndex(f => new { f.TenantId, f.DeletedAt });
            e.HasIndex(f => new { f.GoogleAccountId, f.DriveFileId });
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
            // The public route's only lookup, and the guard against a slug collision becoming two
            // links that resolve to different files depending on row order.
            e.HasIndex(l => l.Slug).IsUnique();
            e.HasIndex(l => new { l.TenantId, l.CreatedAt });
            e.HasOne<StoredFile>()
                .WithMany()
                .HasForeignKey(l => l.StoredFileId)
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
    }
}
