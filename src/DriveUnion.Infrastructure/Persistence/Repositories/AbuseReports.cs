using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Taking a complaint about a public link, and the operator's queue of them.
///
/// <para>Two interfaces on one class because they are two halves of one small thing: an anonymous
/// visitor writes a row and an operator reads it. They are separate interfaces so that the
/// anonymous surface cannot reach the operator's — a controller has to ask for
/// <see cref="IAbuseQueue"/> by name, which is a line in a constructor a reader will notice.</para>
/// </summary>
public sealed class AbuseReports(DriveUnionDbContext db, TimeProvider clock)
    : IAbuseReports, IAbuseQueue
{
    public async Task<AbuseReportResult> FileAsync(
        string slug,
        AbuseKind kind,
        string? note,
        string? reporterEmail,
        string? reporterIpHash,
        CancellationToken cancellationToken)
    {
        // A malformed slug cannot match a generated one, so it is answered without a query — the
        // same shortcut the public page takes, for the same reason.
        if (!SlugGenerator.IsWellFormed(slug))
        {
            return new AbuseReportResult(null, AbuseReportRefusal.UnknownLink);
        }

        var link = await db.ShareLinks
            .AsNoTracking()
            .Where(l => l.Slug == slug)
            .Select(l => new { l.Id, l.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (link is null) return new AbuseReportResult(null, AbuseReportRefusal.UnknownLink);

        // A revoked link is still worth reporting: it is the workspace's history that decides
        // whether the next report is a mistake or a pattern, and a report refused because somebody
        // already pulled the link is a report the operator never sees.
        var open = await db.AbuseReports.CountAsync(
            r => r.ShareLinkId == link.Id && r.Status == AbuseReportStatus.Open,
            cancellationToken);

        if (open >= AbuseReport.MostOpenPerLink)
        {
            return new AbuseReportResult(null, AbuseReportRefusal.AlreadyReported);
        }

        var report = new AbuseReport
        {
            Id = Guid.NewGuid(),
            ShareLinkId = link.Id,
            TenantId = link.TenantId,
            Kind = kind,
            Note = Trimmed(note, AbuseReport.MaxNoteLength),
            ReporterEmail = Trimmed(reporterEmail, AbuseReport.MaxEmailLength),
            ReporterIpHash = reporterIpHash,
            Status = AbuseReportStatus.Open,
            CreatedAt = clock.GetUtcNow(),
        };

        db.AbuseReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);

        return new AbuseReportResult(report.Id, AbuseReportRefusal.None);
    }

    public async Task<IReadOnlyList<AbuseReportView>> ListAsync(
        bool openOnly,
        CancellationToken cancellationToken)
    {
        var reports = await db.AbuseReports
            .AsNoTracking()
            .Where(r => !openOnly || r.Status == AbuseReportStatus.Open)
            .ToListAsync(cancellationToken);

        if (reports.Count == 0) return [];

        // The three things a report is about but does not carry: which link, whose workspace, and
        // what the file was called. Read in one pass each rather than per row.
        var linkIds = reports.Select(r => r.ShareLinkId).Distinct().ToList();

        var links = await db.ShareLinks
            .AsNoTracking()
            .Where(l => linkIds.Contains(l.Id))
            .Join(
                db.StoredFiles.AsNoTracking(),
                l => l.StoredFileId,
                f => f.Id,
                (l, f) => new { l.Id, l.Slug, l.IsActive, FileName = f.Name })
            .ToListAsync(cancellationToken);

        var linkOf = links.ToDictionary(l => l.Id);

        var tenantIds = reports.Select(r => r.TenantId).Distinct().ToList();

        var tenants = await db.Tenants
            .AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.PublicSuspendedAt })
            .ToListAsync(cancellationToken);

        var tenantOf = tenants.ToDictionary(t => t.Id);

        // «How many open reports does this workspace have» is the number that turns one bad file
        // into a decision about the account, so it is beside every row rather than a page away.
        var openPerTenant = reports
            .Where(r => r.Status == AbuseReportStatus.Open)
            .GroupBy(r => r.TenantId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Newest first, in memory: SQLite will not ORDER BY a DateTimeOffset and this must behave
        // the same on it as on Postgres. See ShareLinkService.
        return
        [
            .. reports
                .OrderByDescending(r => r.CreatedAt)
                .Select(r =>
                {
                    var link = linkOf.GetValueOrDefault(r.ShareLinkId);
                    var tenant = tenantOf.GetValueOrDefault(r.TenantId);

                    return new AbuseReportView(
                        r.Id,
                        link?.Slug ?? "—",

                        // The file may have been deleted since — by the customer, or by the operator
                        // acting on this very report. The row outlives it and says so.
                        link?.FileName ?? "—",
                        r.TenantId,
                        tenant?.Name ?? "—",
                        tenant?.PublicSuspendedAt is not null,
                        link is not null && !link.IsActive,
                        openPerTenant.GetValueOrDefault(r.TenantId),
                        r.Kind,
                        r.Note,
                        r.ReporterEmail,
                        r.Status,
                        r.Resolution,
                        r.CreatedAt);
                }),
        ];
    }

    public async Task<int> OpenCountAsync(CancellationToken cancellationToken) =>
        await db.AbuseReports.CountAsync(r => r.Status == AbuseReportStatus.Open, cancellationToken);

    public async Task<bool> UpholdAsync(
        Guid reportId,
        Guid? operatorUserId,
        string? resolution,
        CancellationToken cancellationToken)
    {
        var report = await db.AbuseReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null || report.Status != AbuseReportStatus.Open) return false;

        // The link, not the file. An accusation is not a finding, and revoking stops the public
        // reaching it just as completely as deleting would — while leaving the customer their file
        // and the operator the option of being wrong.
        var link = await db.ShareLinks.FirstOrDefaultAsync(
            l => l.Id == report.ShareLinkId, cancellationToken);

        if (link is not null) link.IsActive = false;

        // Every other open report about the same link closes with it. Ten people reported one file;
        // it is down; there is nothing left for the operator to decide nine more times.
        var siblings = await db.AbuseReports
            .Where(r => r.ShareLinkId == report.ShareLinkId && r.Status == AbuseReportStatus.Open)
            .ToListAsync(cancellationToken);

        foreach (var each in siblings)
        {
            each.Status = AbuseReportStatus.Upheld;
            each.Resolution = Trimmed(resolution, AbuseReport.MaxResolutionLength);
            each.ResolvedByUserId = operatorUserId;
            each.ResolvedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RejectAsync(
        Guid reportId,
        Guid? operatorUserId,
        string? resolution,
        CancellationToken cancellationToken)
    {
        var report = await db.AbuseReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null || report.Status != AbuseReportStatus.Open) return false;

        // One report, and only this one. Ten complaints about a file the operator has judged fine
        // are still ten separate claims, and the next one may be the one that is right.
        report.Status = AbuseReportStatus.Rejected;
        report.Resolution = Trimmed(resolution, AbuseReport.MaxResolutionLength);
        report.ResolvedByUserId = operatorUserId;
        report.ResolvedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> SuspendTenantAsync(
        Guid tenantId,
        Guid? operatorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null || tenant.PublicSuspendedAt is not null) return false;

        tenant.PublicSuspendedAt = clock.GetUtcNow();
        tenant.PublicSuspendedReason = Trimmed(reason, Core.Tenancy.Tenant.MaxSuspensionReasonLength);

        // The links are left alone on purpose. Suspension is a switch on the workspace, so lifting
        // it restores exactly what was there — revoking every link would be a one-way door wearing
        // a two-way label.
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> RestoreTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null || tenant.PublicSuspendedAt is null) return false;

        tenant.PublicSuspendedAt = null;
        tenant.PublicSuspendedReason = null;

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static string? Trimmed(string? value, int max)
    {
        if (value?.Trim() is not { Length: > 0 } typed) return null;

        return typed.Length <= max ? typed : typed[..max];
    }
}
