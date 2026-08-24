using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using DriveUnion.Tests.Services;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The plan services over <see cref="ServiceTestHarness"/>'s real SQLite database.
///
/// <para>SQLite rather than EF's in-memory provider, for the same reason the harness gives: half of
/// what this layer promises is SQL. The storage reserve is a conditional UPDATE whose whole value is
/// that the database decides the race, and the in-memory provider would pass every test here without
/// testing anything.</para>
/// </summary>
internal static class PlanTestSupport
{
    public static TenantPlanService PlanService(
        this ServiceTestHarness harness,
        string defaultPlanCode = Core.Plans.PlanCatalogue.DefaultCode,
        DriveUnionDbContext? context = null) =>
        new(
            context ?? harness.Db,
            harness.Clock,
            Options.Create(new PlansOptions { DefaultPlanCode = defaultPlanCode }));

    /// <summary>
    /// An upload coordinator over a Drive client of the caller's choosing, which
    /// <see cref="ServiceTestHarness.Uploads"/> cannot give because it always supplies its own fake.
    /// </summary>
    public static UploadCoordinator UploadsWith(this ServiceTestHarness harness, IDriveClient drive) =>
        new(harness.Db, drive, new SingleAccountUploadTargetSelector(harness.Db), harness.Clock);

    /// <summary>The tenant's counter and cap as the database holds them, read fresh.</summary>
    public static async Task<(long Used, long Quota)> StorageAsync(
        this ServiceTestHarness harness,
        Guid tenantId) =>
        await TenantStorageMeter.ReadAsync(harness.NewContext(), tenantId, default);
}

/// <summary>
/// A Drive that acknowledges more bytes than the session ever declared.
///
/// <para>This is what a dishonest client looks like from the only vantage point the coordinator is
/// allowed to have. The request body is forwarded to Drive untouched — a counter wrapped round a
/// 96 GB stream is the bug the whole upload path exists to avoid — so the count that proves a
/// declared size was a lie is the far end's acknowledgement, not ours. <see cref="FakeDriveClient"/>
/// cannot produce it, because it is loud rather than forgiving and refuses a body that disagrees
/// with its declared length before it could ever over-acknowledge.</para>
/// </summary>
internal sealed class OverAcknowledgingDriveClient(long acknowledgedLength) : IDriveClient
{
    private static readonly Uri SessionUri = new("https://upload.over-acknowledging.invalid/session/1");

    /// <summary>How many times a chunk was actually pushed at this fake.</summary>
    public int ChunksWritten { get; private set; }

    public Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DriveResumableSession(SessionUri, DateTimeOffset.MaxValue));

    public Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken)
    {
        ChunksWritten++;

        // No metadata: the session is not finished, it has overrun. A completed outcome here would
        // let the coordinator settle a file that must never exist.
        return Task.FromResult(new DriveChunkOutcome(acknowledgedLength, null));
    }

    public Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken) =>
        Task.FromResult(acknowledgedLength);

    public Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken) =>
        Task.FromResult($"folder-{folderName}");

    public Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake exists for the upload path only.");

    public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(new DriveStorageQuota(5L * 1024 * 1024 * 1024 * 1024, 0));
}
