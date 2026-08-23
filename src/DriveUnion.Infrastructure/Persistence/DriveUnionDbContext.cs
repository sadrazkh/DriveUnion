using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
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
    }
}
