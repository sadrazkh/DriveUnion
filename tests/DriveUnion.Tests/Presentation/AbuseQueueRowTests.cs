using System.Text.RegularExpressions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The operator's queue, as words rather than as rows in a table.
///
/// <para>Everything here is a judgement about what an operator is told, and each one is a judgement
/// that could be quietly reversed by somebody tidying the mapping — which is why they are pinned
/// separately from <c>AbuseReportTests</c>, where the subject is the database.</para>
/// </summary>
public class AbuseQueueRowTests
{
    [Fact]
    public void One_report_does_not_announce_itself_as_a_pattern()
    {
        // TenantOpenReports counts this report too, so a workspace with a single complaint arrives
        // here as 1. «1 waiting from this workspace» beside the one report it is counting is a
        // number that reads as news and is not — and the badge is a warning colour.
        Row(openInTenant: 1).OtherReportsText.Should().BeNull();

        // Two is the first number that means anything: somebody has been reported twice, which is
        // the fact that turns one bad file into a decision about the whole workspace.
        Row(openInTenant: 2).OtherReportsText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_operators_column_does_not_repeat_the_reporters_claim_as_a_fact()
    {
        var row = Row(kind: AbuseKind.Copyright);

        // The form's own label is «It is my work, published without permission» — first person,
        // right where a stranger is choosing it, and wrong in a column, where the panel would be
        // stating the claim rather than reporting it.
        row.KindText.Should().NotContain("my work");
        row.KindText.Should().NotContain("کارِ من");

        // Still says which of the five it was, or the column would be decoration.
        var kinds = Enum.GetValues<AbuseKind>().Select(k => Row(kind: k).KindText).ToList();

        kinds.Should().OnlyHaveUniqueItems("an operator triaging cannot tell them apart otherwise");
        kinds.Should().AllSatisfy(k => k.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void A_report_outliving_its_file_still_reads_as_a_report()
    {
        // ListAsync hands «—» through for a link or a file that has been deleted since — by the
        // customer, or by the operator acting on this very report. The row must still render.
        var row = AbuseRowViewModel.From(new AbuseReportView(
            Guid.NewGuid(), "—", "—", Guid.NewGuid(), "—", false, false, 1,
            AbuseKind.Malware, null, null, AbuseReportStatus.Upheld, "Taken down.",
            DateTimeOffset.UnixEpoch));

        row.IsOpen.Should().BeFalse();
        row.StatusText.Should().NotBeNullOrWhiteSpace();
        row.WhenText.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The design's central promise, checked against the markup that would break it.
    ///
    /// <para>There is no privileged viewer in this product and this screen does not add one. An
    /// operator judges a report the way the reporter saw the file: through the public link. A panel
    /// that opened any customer's file on the strength of a stranger's accusation would be a far
    /// larger thing than the problem it solves — so the only address this page may point at, for a
    /// customer's bytes, is <c>/d/{slug}</c>.</para>
    /// </summary>
    [Fact]
    public void The_queue_reaches_a_customers_file_only_through_the_link_the_reporter_used()
    {
        var markup = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src/DriveUnion.Web/Views/AbuseQueue/Index.cshtml"));

        // Every href and every form action on the page, as written.
        var targets = Regex
            .Matches(markup, @"(?:href|action|formaction)=""([^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value)
            .ToList();

        targets.Should().NotBeEmpty("the page has links and forms, or this test is reading nothing");

        foreach (var target in targets)
        {
            // Razor interpolations are left in — «/d/@report.Slug» is the shape being asserted, and
            // resolving it would only turn a check about addresses into a check about one fixture.
            var isPublicLink = target.StartsWith("/d/", StringComparison.Ordinal);
            var isOwnRoute = target.StartsWith("/operator/abuse", StringComparison.Ordinal);

            (isPublicLink || isOwnRoute).Should().BeTrue(
                $"{target} is neither the public link nor this screen's own route; "
                    + "the abuse queue must not acquire a way into a customer's files");
        }

        // And the one that would be easiest to add by accident: the panel's own file routes, which
        // are tenant-scoped and would have to be un-scoped to work from here.
        markup.Should().NotContain("/files/", "that is the customer's own file surface");
    }

    private static AbuseRowViewModel Row(
        int openInTenant = 1,
        AbuseKind kind = AbuseKind.Copyright) =>
        AbuseRowViewModel.From(new AbuseReportView(
            Guid.NewGuid(),
            "kx91mzq4",
            "holiday.mp4",
            Guid.NewGuid(),
            "acme",
            false,
            false,
            openInTenant,
            kind,
            "They took it from my site.",
            "them@example.test",
            AbuseReportStatus.Open,
            null,
            DateTimeOffset.UnixEpoch));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("DriveUnion.slnx was not found above the test binaries.");
    }
}
