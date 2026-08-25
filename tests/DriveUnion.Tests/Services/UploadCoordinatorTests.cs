using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Uploads;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

public class UploadCoordinatorTests
{
    private const int Multiple = UploadChunking.DriveChunkMultiple;

    [Fact]
    public async Task Begin_files_the_upload_under_DriveUnion_slash_the_tenant_slug()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var result = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("quarterly.mp4", "video/mp4", 1024), default);

        result.ChunkSize.Should().Be(UploadChunking.DefaultChunkSize);

        var root = harness.Drive.Folders.Single(f => f.Name == "DriveUnion");
        root.ParentFolderId.Should().BeNull();

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        tenantFolder.ParentFolderId.Should().Be(root.Id, "hygiene now, and M3's files.copy needs a parent");

        var session = await harness.Db.UploadSessions.AsNoTracking().SingleAsync();
        session.Id.Should().Be(result.SessionId);
        session.TenantId.Should().Be(tenant.Id);
        session.GoogleAccountId.Should().Be(account.Id);
        session.Status.Should().Be(UploadSessionStatus.InProgress);

        // The resumable URI is a bearer capability over the operator's Drive: stored here, and
        // nowhere in what Begin hands back to the browser.
        session.DriveResumableUri.Should().NotBeNullOrWhiteSpace();

        var reloaded = await harness.Db.GoogleAccounts.AsNoTracking().SingleAsync();
        reloaded.RootFolderId.Should().Be(root.Id, "the root folder is found once and remembered");
    }

    [Fact]
    public async Task Begin_is_refused_when_no_account_in_the_pool_can_take_the_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount(GoogleAccountStatus.Disconnected);

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("quarterly.mp4", "video/mp4", 1024), default);

        await act.Should().ThrowAsync<UploadRejectedException>();
    }

    [Fact]
    public async Task A_chunk_for_another_tenants_session_is_refused_without_reaching_Drive()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            a.Id, ownerUserId: null, new BeginUploadRequest("payroll.zip", "application/zip", 1024), default);

        using var chunk = new MemoryStream(new byte[1024]);

        var act = () => harness.Uploads().WriteChunkAsync(b.Id, begun.SessionId, chunk, 0, 1024, default);

        // The same exception an unknown session id gets: a distinguishable "not yours" makes session
        // ids worth guessing.
        await act.Should().ThrowAsync<KeyNotFoundException>();

        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.WriteChunk);
        chunk.Position.Should().Be(0, "not one byte of another tenant's upload should have moved");
    }

    [Fact]
    public async Task The_chunk_body_is_handed_to_Drive_unread_and_uncopied()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", Multiple), default);

        using var chunk = new MemoryStream(Payload(Multiple));

        await harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        // Reference equality, because that is the only thing that distinguishes a forwarded stream
        // from a buffered one. A 96 GB upload spooled to memory or disk is a 96 GB bug, and it is
        // invisible on every file small enough to be convenient to test with.
        harness.Drive.LastChunkStream.Should().BeSameAs(chunk);
    }

    [Fact]
    public async Task The_chunk_that_completes_the_file_creates_the_row_the_tenant_can_see()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        const long total = Multiple + 10;

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("quarterly.mp4", "video/mp4", total), default);

        using var first = new MemoryStream(Payload(Multiple));
        var afterFirst = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, first, 0, Multiple, default);

        afterFirst.Status.Should().Be(UploadSessionStatus.InProgress);
        afterFirst.BytesConfirmed.Should().Be(Multiple);
        afterFirst.StoredFileId.Should().BeNull();

        using var last = new MemoryStream(Payload(10));
        var afterLast = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, last, Multiple, 10, default);

        afterLast.Status.Should().Be(UploadSessionStatus.Completed);
        afterLast.BytesConfirmed.Should().Be(total);
        afterLast.StoredFileId.Should().NotBeNull();

        var stored = await harness.Db.StoredFiles.AsNoTracking().SingleAsync();
        stored.Id.Should().Be(afterLast.StoredFileId!.Value);
        stored.TenantId.Should().Be(tenant.Id);
        stored.GoogleAccountId.Should().Be(account.Id);
        stored.SizeBytes.Should().Be(total);

        var listing = await harness.Files().ListAsync(tenant.Id, folderId: null, nameQuery: null, default);
        listing.Should().ContainSingle();
        listing[0].Name.Should().Be("quarterly.mp4");
    }

    [Fact]
    public async Task Replaying_the_final_chunk_does_not_produce_a_second_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("quarterly.mp4", "video/mp4", 1024), default);

        using var only = new MemoryStream(Payload(1024));
        var completed = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, only, 0, 1024, default);

        using var replay = new MemoryStream(Payload(1024));
        var again = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, replay, 0, 1024, default);

        again.Status.Should().Be(UploadSessionStatus.Completed);
        again.StoredFileId.Should().Be(completed.StoredFileId);
        (await harness.Db.StoredFiles.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_chunk_that_breaks_the_256_KiB_rule_is_rejected_before_Drive_sees_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", 4 * Multiple), default);

        using var ragged = new MemoryStream(Payload(1000));

        // Drive answers a badly sized chunk by quietly not acknowledging it, which looks exactly
        // like a stalled network. Better a 400 now than a support ticket later.
        var act = () => harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, ragged, 0, 1000, default);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.WriteChunk);
    }

    [Fact]
    public async Task An_expired_session_fails_once_with_a_reason_the_client_can_act_on()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", Multiple), default);

        harness.Clock.Advance(TimeSpan.FromDays(8));

        using var chunk = new MemoryStream(Payload(Multiple));
        var progress = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        progress.Status.Should().Be(UploadSessionStatus.Failed);
        progress.FailureReason.Should().NotBeNullOrWhiteSpace();

        // Marked failed once, up front — not left to discover the dead session URI chunk by chunk.
        harness.Drive.Calls.Should().NotContain(c => c.Operation == FakeDriveOperation.WriteChunk);

        using var another = new MemoryStream(Payload(Multiple));
        var second = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, another, 0, Multiple, default);
        second.Status.Should().Be(UploadSessionStatus.Failed);
        second.FailureReason.Should().Be(progress.FailureReason);
    }

    [Fact]
    public async Task A_session_Drive_has_forgotten_is_failed_rather_than_retried_for_ever()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", Multiple), default);

        harness.Drive.FailNext(
            FakeDriveOperation.WriteChunk,
            new DriveUploadSessionExpiredException("This session is gone."));

        using var chunk = new MemoryStream(Payload(Multiple));
        var progress = await harness.Uploads()
            .WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        progress.Status.Should().Be(UploadSessionStatus.Failed);
        progress.FailureReason.Should().Contain("This session is gone.");
    }

    [Fact]
    public async Task A_rate_limit_that_survived_the_transport_reaches_the_caller()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", Multiple), default);

        harness.Drive.RateLimitNext(FakeDriveOperation.WriteChunk, TimeSpan.FromSeconds(30));

        using var chunk = new MemoryStream(Payload(Multiple));
        var act = () => harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        // Backoff belongs in the transport. By the time it gets here the retries are spent, and the
        // session stays resumable so the client can come back.
        await act.Should().ThrowAsync<DriveRateLimitedException>();

        var session = await harness.Db.UploadSessions.AsNoTracking().SingleAsync();
        session.Status.Should().Be(UploadSessionStatus.InProgress);
    }

    [Fact]
    public async Task Progress_is_what_Google_acknowledged_not_what_we_believe_we_sent()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();
        const long total = 2 * Multiple;

        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("big.bin", "application/octet-stream", total), default);

        using var chunk = new MemoryStream(Payload(Multiple));
        await harness.Uploads().WriteChunkAsync(tenant.Id, begun.SessionId, chunk, 0, Multiple, default);

        // Our own row now lies about how much arrived — the shape of a chunk that died on the wire
        // after we had counted it.
        var row = await harness.Db.UploadSessions.SingleAsync();
        row.BytesConfirmed = total;
        await harness.Db.SaveChangesAsync();

        var progress = await harness.Uploads().GetProgressAsync(tenant.Id, begun.SessionId, default);

        progress.BytesConfirmed.Should().Be(Multiple);
        harness.Drive.Calls.Should().Contain(c => c.Operation == FakeDriveOperation.GetConfirmedLength);

        var corrected = await harness.Db.UploadSessions.AsNoTracking().SingleAsync();
        corrected.BytesConfirmed.Should().Be(Multiple, "the correction is written back, not just returned");
    }

    [Fact]
    public async Task Progress_for_another_tenants_session_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        harness.SeedAccount();

        var begun = await harness.Uploads().BeginAsync(
            a.Id, ownerUserId: null, new BeginUploadRequest("payroll.zip", "application/zip", 1024), default);

        var act = () => harness.Uploads().GetProgressAsync(b.Id, begun.SessionId, default);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task A_browser_that_sends_no_content_type_still_gets_a_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("mystery.bin", "  ", 1024), default);

        var session = await harness.Db.UploadSessions.AsNoTracking().SingleAsync();
        session.MimeType.Should().Be("application/octet-stream");
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }
}
