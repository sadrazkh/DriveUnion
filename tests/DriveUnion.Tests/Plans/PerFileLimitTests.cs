using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The per-file limit: refused where the size is first claimed, and again where it is proved.
/// </summary>
public class PerFileLimitTests
{
    private const int Multiple = UploadChunking.DriveChunkMultiple;

    [Fact]
    public async Task The_limit_lives_in_the_coordinator_and_refuses_before_Drive_is_touched()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.MaxFileBytes, 4 * Multiple, "Small tier.", null, default);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("huge.mkv", "video/x-matroska", 100 * Multiple), default);

        var refusal = (await act.Should().ThrowAsync<PlanLimitExceededException>()).Which;

        refusal.Limit.Should().Be(PlanLimit.File);
        refusal.Code.Should().Be("file_too_large_for_plan");
        refusal.CapBytes.Should().Be(4 * Multiple);
        refusal.RequestedBytes.Should().Be(100 * Multiple);

        // The check is in IUploadCoordinator.BeginAsync rather than in a controller because there are
        // already three callers of this path and a check in one of them is a check the other two do
        // not have. Nothing was opened at the far end, and no session row exists to resume.
        harness.Drive.Calls.Should().BeEmpty();
        (await harness.Db.UploadSessions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_refused_upload_spends_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var plans = harness.PlanService();
        await plans.SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.MaxFileBytes, 4 * Multiple, "Small tier.", null, default);

        var before = await harness.StorageAsync(tenant.Id);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("huge.mkv", "video/x-matroska", 100 * Multiple), default);

        await act.Should().ThrowAsync<PlanLimitExceededException>();

        var after = await harness.StorageAsync(tenant.Id);

        // The size check runs ahead of the storage reserve on purpose: refusing first means there is
        // nothing to unwind, and an unwind that fails leaves the customer paying rent on a file that
        // was never accepted.
        after.Used.Should().Be(before.Used);
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_accepted()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.MaxFileBytes, Multiple, "Exact.", null, default);

        // The limit is a ceiling, not a strict bound. It is stated here because the two are one
        // character apart and the difference is a customer whose 2 GB file is refused by a 2 GB plan.
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("exact.bin", "application/octet-stream", Multiple), default);

        begun.SessionId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_lying_declared_size_is_caught_by_the_byte_counter_and_kills_the_session()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        const long declared = 4 * Multiple;

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.MaxFileBytes, declared, "Small tier.", null, default);

        // Declares a size inside the limit, and then pushes far more than that. The count that
        // catches it is the far end's acknowledgement: the request body is forwarded untouched, so
        // nothing on this box may measure it.
        var drive = new OverAcknowledgingDriveClient(acknowledgedLength: 40 * Multiple);
        var coordinator = harness.UploadsWith(drive);

        var begun = await coordinator.BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("liar.bin", "application/octet-stream", declared), default);

        var reserved = await harness.StorageAsync(tenant.Id);
        reserved.Used.Should().Be(declared, "the reservation was taken against the claim");

        using var body = new MemoryStream(new byte[Multiple]);
        var act = () => coordinator.WriteChunkAsync(tenant.Id, begun.SessionId, body, 0, Multiple, default);

        var refusal = (await act.Should().ThrowAsync<PlanLimitExceededException>()).Which;
        refusal.Limit.Should().Be(PlanLimit.File);
        refusal.RequestedBytes.Should().Be(40 * Multiple);

        var session = await harness.NewContext().UploadSessions.AsNoTracking().SingleAsync();
        session.Status.Should().Be(UploadSessionStatus.Failed, "an overrun session is over, not paused");

        var afterwards = await harness.StorageAsync(tenant.Id);
        afterwards.Used.Should().Be(
            0, "a session that failed gives its reservation back, or the tenant pays rent for ever");

        (await harness.NewContext().StoredFiles.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_plan_refuses_before_the_pool_does()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        // Every account in the pool is unusable, so the pool would refuse this too.
        harness.SeedAccount(GoogleAccountStatus.Disconnected);

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 0, "Out of room.", null, default);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("anything.bin", "application/octet-stream", 1024), default);

        // The customer's own cap is true whichever way our supply is going, and they can act on it.
        // «آپلود موقتاً در دسترس نیست — تا ساعت ۱۰:۳۰ دوباره تلاش کنید» promises a retry that will not
        // help them, so it must not be the answer they get.
        var refusal = (await act.Should().ThrowAsync<PlanLimitExceededException>()).Which;

        refusal.Limit.Should().Be(PlanLimit.Storage);
        refusal.Should().NotBeOfType<UploadRejectedException>();
    }

    [Fact]
    public async Task The_storage_refusal_carries_the_three_figures_its_body_needs()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 3 * Multiple, "Tight.", null, default);

        await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("first.bin", "application/octet-stream", 2 * Multiple), default);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("second.bin", "application/octet-stream", 2 * Multiple), default);

        var refusal = (await act.Should().ThrowAsync<PlanLimitExceededException>()).Which;

        refusal.Code.Should().Be("tenant_quota_exceeded");
        refusal.CapBytes.Should().Be(3 * Multiple);
        refusal.UsedBytes.Should().Be(2 * Multiple, "the in-flight session counts against the cap");
        refusal.RequestedBytes.Should().Be(2 * Multiple);
    }

    [Fact]
    public async Task An_in_flight_session_is_counted_so_parallel_uploads_cannot_overshoot()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 10 * Multiple, "Ten chunks.", null, default);

        // Four sessions of three, into a cap of ten. Without counting what is in flight all four
        // would pass the check and land at twelve — a bug that only appears under a real user with a
        // real connection, which is to say in production.
        for (var i = 0; i < 3; i++)
        {
            await harness.Uploads().BeginAsync(
                tenant.Id,
                ownerUserId: null, new BeginUploadRequest($"part-{i}.bin", "application/octet-stream", 3 * Multiple),
                default);
        }

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("part-3.bin", "application/octet-stream", 3 * Multiple), default);

        await act.Should().ThrowAsync<PlanLimitExceededException>();

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(9 * Multiple);
    }

    [Fact]
    public async Task A_completed_upload_settles_the_reservation_at_the_size_Drive_reports()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        const long declared = 2 * Multiple;

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("real.bin", "application/octet-stream", declared), default);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(declared);

        using var first = new MemoryStream(new byte[Multiple]);
        await harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, first, 0, Multiple, default);

        using var last = new MemoryStream(new byte[Multiple]);
        var finished = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, last, Multiple, Multiple, default);

        finished.Status.Should().Be(UploadSessionStatus.Completed);

        var stored = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync();

        // The reservation is replaced by what Drive says it stored, which is the only figure that is
        // evidence — our own record is of what we sent.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(stored.SizeBytes);
    }

    [Fact]
    public async Task An_expired_session_gives_its_reservation_back_exactly_once()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        const long declared = 2 * Multiple;

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("abandoned.bin", "application/octet-stream", declared), default);

        harness.Clock.Advance(TimeSpan.FromDays(8));

        using var chunk = new MemoryStream(new byte[Multiple]);
        await harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);

        // Twice, because a release that runs on every subsequent poll would drive the counter
        // negative and negative usage is free storage until somebody notices.
        await harness.Uploads().GetProgressAsync(tenant.Id, begun.SessionId, default);
        using var again = new MemoryStream(new byte[Multiple]);
        await harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, again, 0, Multiple, default);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }

    [Fact]
    public async Task A_pool_that_cannot_take_the_file_gives_the_reservation_back()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount(GoogleAccountStatus.Disconnected);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("nowhere.bin", "application/octet-stream", 4 * Multiple), default);

        await act.Should().ThrowAsync<UploadRejectedException>();

        // The reservation is durable across the Google round trip on purpose — that is what stops two
        // uploads spending the same free bytes — so the compensating release is the only thing that
        // can give it back, and a caller that forgot would leave the tenant paying for nothing.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }

    [Fact]
    public async Task Drive_refusing_to_open_a_session_gives_the_reservation_back()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        harness.Drive.FailNext(
            FakeDriveOperation.BeginResumableUpload,
            new Core.Abstractions.DriveApiException("Drive said no."));

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("unlucky.bin", "application/octet-stream", 4 * Multiple), default);

        await act.Should().ThrowAsync<Core.Abstractions.DriveApiException>();

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }
}
