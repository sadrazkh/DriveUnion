using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Somebody telling the operator that a public link is hosting something it should not.
///
/// <para>The failure this prevents is not legal, it is operational: every customer's files sit in a
/// Google account the operator owns, and a file reported to <i>Google</i> gets that account
/// suspended — taking down every workspace routed onto it, not just the one at fault. The only
/// thing that stops it is the operator hearing about the file first, which is what this queue is.
/// </para>
/// </summary>
public class AbuseReportTests
{
    private static AbuseReports Queue(ServiceTestHarness harness) => new(harness.Db, harness.Clock, harness.Push);

    [Fact]
    public async Task A_report_reaches_the_operators_queue()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "pirated.mp4");
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var filed = await Queue(harness).FileAsync(
            "kx91mzq4", AbuseKind.Copyright, "This is my film.", "them@example.test", "hash", default);

        filed.Refusal.Should().Be(AbuseReportRefusal.None);

        var queued = await Queue(harness).ListAsync(openOnly: true, default);

        var report = queued.Should().ContainSingle().Subject;
        report.Slug.Should().Be("kx91mzq4");
        report.FileName.Should().Be("pirated.mp4");
        report.TenantName.Should().Be("acme");
        report.Kind.Should().Be(AbuseKind.Copyright);
    }

    [Fact]
    public async Task An_unknown_slug_and_a_real_one_are_both_accepted_in_silence()
    {
        await using var harness = ServiceTestHarness.Create();

        // Refused, and the caller is told nothing that separates this from a real link — see
        // IAbuseReports. A form that answered «no such link» would confirm which slugs exist to
        // anybody willing to type, which is the enumeration the public card's single refusal exists
        // to prevent. It would be a strange thing to reopen through the abuse form of all places.
        var filed = await Queue(harness).FileAsync(
            "zzzzzzzz", AbuseKind.Other, "nothing here", null, null, default);

        filed.Refusal.Should().Be(AbuseReportRefusal.UnknownLink);
        filed.ReportId.Should().BeNull();

        (await harness.Db.AbuseReports.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task One_person_cannot_bury_the_queue_under_the_same_link()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        for (var i = 0; i < AbuseReport.MostOpenPerLink; i++)
        {
            (await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Other, $"n{i}", null, null, default))
                .Refusal.Should().Be(AbuseReportRefusal.None);
        }

        // Ten identical complaints tell the operator exactly what one does, and the queue is the
        // operator's attention — the thing somebody would be attacking if they wanted the real
        // complaints buried.
        (await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Other, "again", null, null, default))
            .Refusal.Should().Be(AbuseReportRefusal.AlreadyReported);

        (await harness.Db.AbuseReports.CountAsync()).Should().Be(AbuseReport.MostOpenPerLink);
    }

    [Fact]
    public async Task Upholding_takes_the_link_down_and_leaves_the_file_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var filed = await Queue(harness).FileAsync(
            "kx91mzq4", AbuseKind.Malware, "it is a trojan", null, null, default);

        (await Queue(harness).UpholdAsync(filed.ReportId!.Value, Guid.NewGuid(), "Taken down.", default))
            .Should().BeTrue();

        // Revoked rather than deleted. An accusation is not a finding: this stops the public
        // reaching it just as completely, and leaves the customer their file and the operator the
        // option of having been wrong.
        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .IsActive.Should().BeFalse();

        (await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id))
            .DeletedAt.Should().BeNull();

        (await Queue(harness).ListAsync(openOnly: true, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Upholding_one_report_closes_the_others_about_the_same_link()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var first = await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Copyright, "a", null, null, default);
        await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Copyright, "b", null, null, default);
        await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Copyright, "c", null, null, default);

        await Queue(harness).UpholdAsync(first.ReportId!.Value, Guid.NewGuid(), "Down.", default);

        // Three people reported one file; it is down; there is nothing left for the operator to
        // decide twice more.
        (await Queue(harness).OpenCountAsync(default)).Should().Be(0);

        (await harness.Db.AbuseReports.AsNoTracking().ToListAsync())
            .Should().OnlyContain(r => r.Status == AbuseReportStatus.Upheld);
    }

    [Fact]
    public async Task Rejecting_closes_one_report_and_only_that_one()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var first = await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Other, "a", null, null, default);
        await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Illegal, "b", null, null, default);

        await Queue(harness).RejectAsync(first.ReportId!.Value, Guid.NewGuid(), "Nothing wrong.", default);

        // Ten complaints about a file the operator has judged fine are still ten separate claims,
        // and the next one may be the one that is right.
        (await Queue(harness).OpenCountAsync(default)).Should().Be(1);

        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .IsActive.Should().BeTrue("a rejected report touches nothing");
    }

    [Fact]
    public async Task Suspending_a_workspace_stops_every_public_link_it_has()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var first = harness.SeedFile(tenant.Id, account.Id, "a.mp4");
        var second = harness.SeedFile(tenant.Id, account.Id, "b.mp4");
        harness.SeedLink(tenant.Id, first.Id, "kx91mzq4");
        harness.SeedLink(tenant.Id, second.Id, "aa22bb33");

        (await harness.PublicLinks().ResolveAsync("kx91mzq4", default)).IsAvailable.Should().BeTrue();

        (await Queue(harness).SuspendTenantAsync(tenant.Id, Guid.NewGuid(), "Repeat offender.", default))
            .Should().BeTrue();

        // Both, and without touching either link — the blunt instrument, for when one file is not
        // the problem.
        (await harness.PublicLinks().ResolveAsync("kx91mzq4", default)).IsAvailable.Should().BeFalse();
        (await harness.PublicLinks().ResolveAsync("aa22bb33", default)).IsAvailable.Should().BeFalse();

        // And the streaming route independently, because a visitor holding a direct address never
        // loads the card: a suspension enforced only on the page would stop the button and leave the
        // bytes served.
        (await harness.PublicLinks().ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
    }

    [Fact]
    public async Task Suspension_takes_nothing_away_from_the_owner()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        await Queue(harness).SuspendTenantAsync(tenant.Id, Guid.NewGuid(), "Under review.", default);

        // No file deleted, no link revoked. An accusation is not a finding, and a control that
        // destroyed data on one would be a control nobody could afford to use quickly — which is the
        // one thing it has to be.
        (await harness.Db.StoredFiles.AsNoTracking().SingleAsync()).DeletedAt.Should().BeNull();
        (await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id))
            .IsActive.Should().BeTrue();

        // And lifting it restores exactly what was there, because the links were never touched.
        (await Queue(harness).RestoreTenantAsync(tenant.Id, default)).Should().BeTrue();

        (await harness.PublicLinks().ResolveAsync("kx91mzq4", default)).IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_report_outlives_the_link_it_was_about()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        await Queue(harness).FileAsync("kx91mzq4", AbuseKind.Copyright, "mine", null, null, default);

        harness.Db.ShareLinks.Remove(link);
        await harness.Db.SaveChangesAsync();

        // A link revoked and purged is precisely when somebody asks what was reported and what was
        // done about it. A foreign key would have taken the answer away with the thing it was about.
        var queued = await Queue(harness).ListAsync(openOnly: false, default);

        queued.Should().ContainSingle();
        queued[0].Slug.Should().Be("—", "the link is gone and the row says so rather than inventing one");
        queued[0].TenantName.Should().Be("acme", "whose it was is on the report itself");
    }
}
