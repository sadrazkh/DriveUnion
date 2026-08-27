using System.Net;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Tests.Services;
using DriveUnion.Tests.Trash;
using DriveUnion.Tests.Uploads;
using FluentAssertions;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// What this product is willing to wake a phone for, asserted from the code that finishes the work.
///
/// <para><b>The omissions are the design.</b> A notification for something the reader has already
/// seen confirmed on screen teaches them to dismiss the next one unread, and the next one is the
/// abuse report that is racing Google. So an ordinary upload raises nothing — its progress is in the
/// dock on every screen in the panel and the phone is in the reader's hand — and neither does a
/// share link, a restore, or a fetch that is merely being retried.</para>
///
/// <para>What is left is the three cases where the person who asked is expected to be somewhere
/// else. This file is where that list is held to, in both directions.</para>
/// </summary>
public class NotifiableEventTests
{
    private const string Url = "https://files.example.test/report.pdf";

    // ── a link-upload ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A fetch that lands tells the person who asked for it.</b>
    ///
    /// <para>The one outcome in the product where nobody is looking at a screen that could have said
    /// it: the whole reason this feature exists is that the customer's machine can be asleep by now.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_link_upload_that_finishes_tells_the_person_who_asked()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var owner = Guid.NewGuid();

        await harness.Fetches().StartAsync(tenant.Id, owner, Url, null, default);

        var source = new StubSource(new byte[2048], "report.pdf", "application/pdf");

        await harness.Fetcher(source).RunOnceAsync(5, default);

        harness.RaisedNotifications().Should().Equal(
            [new PushEvent(PushEventKind.RemoteFetchCompleted, PushAudience.Person(tenant.Id, owner))]);
    }

    /// <summary>
    /// A fetch queued with nobody named reaches the workspace instead of nobody.
    ///
    /// <para><c>OwnerUserId</c> is nullable — a fetch started through a path that had no principal
    /// carries none — and an audience built from it unguarded would be a notification addressed to
    /// <c>Guid.Empty</c>, which matches no device and fails silently.</para>
    /// </summary>
    [Fact]
    public async Task A_link_upload_with_no_owner_tells_the_workspace()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.Fetches().StartAsync(tenant.Id, null, Url, null, default);
        await harness.Fetcher(new StubSource(new byte[2048], "x.bin", "application/octet-stream"))
            .RunOnceAsync(5, default);

        harness.RaisedNotifications().Should().Equal(
            [new PushEvent(PushEventKind.RemoteFetchCompleted, PushAudience.Workspace(tenant.Id))]);
    }

    /// <summary>
    /// <b>A fetch is announced when it is over, and not when it is retried.</b>
    ///
    /// <para>A retry is not news: the customer asked for a file and the file is still coming. A phone
    /// buzzing three times for one fetch that eventually worked is exactly how somebody learns to
    /// turn notifications off — and the row goes back to <c>Queued</c> between attempts, so a raise
    /// in the failure path unguarded would do precisely that.</para>
    /// </summary>
    [Fact]
    public async Task A_link_upload_says_nothing_until_it_is_given_up_on()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.Fetches().StartAsync(tenant.Id, null, Url, null, default);

        var source = new StubSource([], "x", "text/plain") { Status = HttpStatusCode.NotFound };

        for (var attempt = 1; attempt < RemoteFetch.MaxAttempts; attempt++)
        {
            await harness.Fetcher(source).RunOnceAsync(1, default);

            harness.RaisedNotifications().Should().BeEmpty(
                $"attempt {attempt} put the fetch back in the queue rather than ending it");
        }

        await harness.Fetcher(source).RunOnceAsync(1, default);

        harness.RaisedNotifications().Should().Equal(
            [new PushEvent(PushEventKind.RemoteFetchFailed, PushAudience.Workspace(tenant.Id))]);
    }

    // ── a queued deletion ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A queued deletion tells the workspace once, when it is finished.</b>
    ///
    /// <para>Once, and not once per file: the runner moves files in passes and the transition to
    /// Completed happens on the pass that finds nothing left. A raise inside that loop would be one
    /// notification per file, which for the folder of forty thousand this feature exists for is a
    /// phone that has to be turned off.</para>
    /// </summary>
    [Fact]
    public async Task A_queued_deletion_tells_the_workspace_once_when_it_is_finished()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await harness.SeedUploadedFileAsync(tenant, account, name: $"clip-{i}.mp4")).Id);
        }

        await harness.Deletions().DeleteFilesAsync(tenant.Id, ids, default);

        // The press itself says nothing. Everything the customer can see already happened in their
        // own request, on the screen they are looking at.
        harness.RaisedNotifications().Should().BeEmpty();

        // One file at a time, so the loop really does run more than once.
        for (var i = 0; i < ids.Count; i++)
        {
            await harness.Deleter().RunOnceAsync(1, default);

            harness.RaisedNotifications().Should().BeEmpty("the job is not finished yet");
        }

        await harness.Deleter().RunOnceAsync(1, default);

        harness.RaisedNotifications().Should().Equal(
            [new PushEvent(PushEventKind.DeletionCompleted, PushAudience.Workspace(tenant.Id), ids.Count)]);
    }

    // ── an abuse report ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A new complaint reaches the operator, and only the operator.</b>
    ///
    /// <para>The one notification here that is not a courtesy. A file reported to Google gets the
    /// pool account holding it suspended, and that account holds the files of every workspace routed
    /// onto it — so the operator hearing about it first is the whole of what the queue is for. The
    /// sidebar badge already says how many are waiting; it says it to somebody who is looking at the
    /// panel, which at three in the morning is nobody.</para>
    ///
    /// <para>The audience carries no tenant. The workspace that was reported must not be told that
    /// it was.</para>
    /// </summary>
    [Fact]
    public async Task A_new_abuse_report_reaches_the_operator_and_names_no_workspace()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "pirated.mp4");
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var reports = new Infrastructure.Persistence.Repositories.AbuseReports(
            harness.Db, harness.Clock, harness.Push);

        var filed = await reports.FileAsync("kx91mzq4", AbuseKind.Copyright, "mine", null, null, default);

        filed.Refusal.Should().Be(AbuseReportRefusal.None);

        harness.RaisedNotifications().Should().Equal(
            [new PushEvent(PushEventKind.AbuseReportFiled, PushAudience.OperatorStaff)]);
    }

    /// <summary>
    /// A report that was refused raises nothing.
    ///
    /// <para>A slug that names no link, and a link already at its cap of open complaints, both end
    /// with no row written — so a notification would be the operator opening a queue that does not
    /// have it. It is also the shape of the attack the cap exists for: the queue is the operator's
    /// attention, and a form anybody can submit must not be a way to ring their phone.</para>
    /// </summary>
    [Fact]
    public async Task A_report_that_was_not_written_raises_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "pirated.mp4");
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var reports = new Infrastructure.Persistence.Repositories.AbuseReports(
            harness.Db, harness.Clock, harness.Push);

        (await reports.FileAsync("zzzzzzzz", AbuseKind.Other, null, null, null, default))
            .Refusal.Should().Be(AbuseReportRefusal.UnknownLink);

        harness.RaisedNotifications().Should().BeEmpty();

        for (var i = 0; i < AbuseReport.MostOpenPerLink; i++)
        {
            await reports.FileAsync("kx91mzq4", AbuseKind.Other, null, null, null, default);
        }

        harness.RaisedNotifications().Should().HaveCount(
            AbuseReport.MostOpenPerLink,
            "each of those was written");

        (await reports.FileAsync("kx91mzq4", AbuseKind.Other, null, null, null, default))
            .Refusal.Should().Be(AbuseReportRefusal.AlreadyReported);

        harness.RaisedNotifications().Should().BeEmpty("nothing was written, so there is nothing to say");
    }

    // ── what is deliberately silent ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>An ordinary upload says nothing.</b>
    ///
    /// <para>The dock draws its progress on every screen in the panel and the phone is in the
    /// reader's hand: there is no moment at which a notification would tell them something they are
    /// not already looking at. This is the omission most likely to be «fixed» by somebody who has not
    /// read the argument, which is why it is a test rather than a paragraph.</para>
    /// </summary>
    [Fact]
    public async Task An_ordinary_upload_raises_nothing_because_the_reader_is_watching_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        await harness.SeedUploadedFileAsync(tenant, account, name: "holiday.mp4");

        var uploads = harness.Uploads();

        var begun = await uploads.BeginAsync(
            tenant.Id,
            Guid.NewGuid(),
            new BeginUploadRequest("report.pdf", "application/pdf", 8, null),
            default);

        await uploads.WriteChunkAsync(tenant.Id, begun.SessionId, new MemoryStream(new byte[8]), 0, 8, default);

        harness.RaisedNotifications().Should().BeEmpty();
    }

    /// <summary>
    /// A share link says nothing either, and for the same reason: the address is on the screen the
    /// moment it is minted, with a button beside it that copies it.
    /// </summary>
    [Fact]
    public async Task Creating_a_share_link_raises_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "report.pdf");

        await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(file.Id, null, null), default);

        harness.RaisedNotifications().Should().BeEmpty();
    }
}
