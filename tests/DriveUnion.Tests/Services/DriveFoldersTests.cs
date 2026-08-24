using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

public class DriveFoldersTests
{
    [Fact]
    public async Task A_persons_home_is_their_own_folder_inside_their_tenants()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var home = await harness.Folders().HomeAsync(account.Id, tenant.Id, user, default);

        var root = harness.Drive.Folders.Single(f => f.Name == "DriveUnion");
        root.ParentFolderId.Should().BeNull();

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        tenantFolder.ParentFolderId.Should().Be(root.Id);

        var homeFolder = harness.Drive.Folders.Single(f => f.Id == home);
        homeFolder.Name.Should().Be($"u-{user:N}", "a folder path with files in it is named by the "
            + "one thing about a person that cannot change under it");
        homeFolder.ParentFolderId.Should().Be(tenantFolder.Id);
    }

    [Fact]
    public async Task The_trash_is_a_folder_of_ours_beside_the_home()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folders = harness.Folders();
        var home = await folders.HomeAsync(account.Id, tenant.Id, user, default);
        var trash = await folders.TrashAsync(account.Id, tenant.Id, user, default);

        var trashFolder = harness.Drive.Folders.Single(f => f.Id == trash);
        trashFolder.Name.Should().Be(".trash");
        trashFolder.ParentFolderId.Should().Be(home, "retention is ours to run, which it cannot be "
            + "inside a trash Google empties on a schedule it does not tell us about");
    }

    [Fact]
    public async Task No_owner_resolves_to_the_tenant_folder_the_product_started_with()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var folders = harness.Folders();
        var home = await folders.HomeAsync(account.Id, tenant.Id, null, default);
        var trash = await folders.TrashAsync(account.Id, tenant.Id, null, default);

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        home.Should().Be(tenantFolder.Id);

        harness.Drive.Folders.Should().NotContain(
            f => f.Name.StartsWith("u-", StringComparison.Ordinal),
            "a caller with no person must not have one invented for it");

        harness.Drive.Folders.Single(f => f.Id == trash).ParentFolderId.Should().Be(tenantFolder.Id);
    }

    [Fact]
    public async Task The_second_ask_for_the_same_folder_costs_no_request()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var first = await harness.Folders().HomeAsync(account.Id, tenant.Id, user, default);

        // Three levels, one find-or-create each: DriveUnion, acme, and the person.
        EnsureFolderCalls(harness).Should().Be(3);

        // A different instance, as a second request in the same process would be. The cache is what
        // they share, and it is the whole point: the request that is not made.
        var second = await harness.Folders(harness.NewContext())
            .HomeAsync(account.Id, tenant.Id, user, default);

        second.Should().Be(first);
        EnsureFolderCalls(harness).Should().Be(3, "the answer was already known");
    }

    [Fact]
    public async Task The_root_folder_survives_a_restart_on_the_accounts_own_row()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        await harness.Folders().HomeAsync(account.Id, tenant.Id, Guid.NewGuid(), default);

        var root = harness.Drive.Folders.Single(f => f.Name == "DriveUnion");

        var reloaded = await harness.NewContext().GoogleAccounts.FindAsync(account.Id);
        reloaded!.RootFolderId.Should().Be(root.Id, "the row is the half of the cache that outlives "
            + "the process");
    }

    [Fact]
    public async Task Two_people_in_one_tenant_get_two_folders()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var maryam = Guid.NewGuid();
        var reza = Guid.NewGuid();

        var folders = harness.Folders();
        var hers = await folders.HomeAsync(account.Id, tenant.Id, maryam, default);
        var his = await folders.HomeAsync(account.Id, tenant.Id, reza, default);
        var herTrash = await folders.TrashAsync(account.Id, tenant.Id, maryam, default);
        var hisTrash = await folders.TrashAsync(account.Id, tenant.Id, reza, default);

        hers.Should().NotBe(his);
        herTrash.Should().NotBe(hisTrash);

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        harness.Drive.Folders.Single(f => f.Id == hers).ParentFolderId.Should().Be(tenantFolder.Id);
        harness.Drive.Folders.Single(f => f.Id == his).ParentFolderId.Should().Be(tenantFolder.Id);

        // Neither person's trash is inside the other's home, which is the only way one of them could
        // reach the other's files by holding a folder id.
        harness.Drive.Folders.Single(f => f.Id == herTrash).ParentFolderId.Should().Be(hers);
        harness.Drive.Folders.Single(f => f.Id == hisTrash).ParentFolderId.Should().Be(his);

        // The second person costs one request, not three: the two levels above them were known.
        EnsureFolderCalls(harness).Should().Be(6);
    }

    [Fact]
    public async Task The_same_person_in_two_tenants_is_two_folders()
    {
        await using var harness = ServiceTestHarness.Create();
        var acme = harness.SeedTenant("acme");
        var globex = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var folders = harness.Folders();
        var inAcme = await folders.HomeAsync(account.Id, acme.Id, user, default);
        var inGlobex = await folders.HomeAsync(account.Id, globex.Id, user, default);

        inAcme.Should().NotBe(inGlobex, "the tenant is part of the answer, not decoration on it");
    }

    [Fact]
    public async Task Two_callers_racing_for_one_folder_produce_one_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        var slow = new HeldOpenDriveClient(harness.Drive);
        var first = new DriveFolders(harness.Db, slow, harness.FolderCache);
        var second = new DriveFolders(harness.NewContext(), slow, harness.FolderCache);

        var a = Task.Run(() => first.HomeAsync(account.Id, tenant.Id, user, default));
        await slow.Entered;

        var b = Task.Run(() => second.HomeAsync(account.Id, tenant.Id, user, default));

        // Long enough for the second caller to reach the gate the first is holding. Nothing below
        // depends on it winning that race — a second caller that arrives late finds the answer
        // cached, which is the same outcome by the shorter road.
        await Task.Delay(50);

        slow.Release();

        var ids = await Task.WhenAll(a, b);

        ids[0].Should().Be(ids[1]);

        // The assertion that matters. Drive is happy to hold two folders with one name, and if both
        // callers had got as far as creating, this would be 2 and every later resolve would pick one
        // of them arbitrarily — half of somebody's files in each, reported by nothing.
        harness.Drive.Folders.Count(f => f.Name == $"u-{user:N}").Should().Be(1);
    }

    [Fact]
    public async Task A_resolve_that_failed_is_not_remembered_as_an_answer()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var user = Guid.NewGuid();

        harness.Drive.RateLimitNext(FakeDriveOperation.EnsureFolder, TimeSpan.FromSeconds(30));

        var folders = harness.Folders();
        var act = () => folders.HomeAsync(account.Id, tenant.Id, user, default);

        await act.Should().ThrowAsync<DriveRateLimitedException>();

        // Nothing is written until there is an id to write, so a rate limit costs this upload and
        // not every upload until the process is restarted.
        var home = await folders.HomeAsync(account.Id, tenant.Id, user, default);

        harness.Drive.Folders.Single(f => f.Id == home).Name.Should().Be($"u-{user:N}");
    }

    private static int EnsureFolderCalls(ServiceTestHarness harness) =>
        harness.Drive.Calls.Count(c => c.Operation == FakeDriveOperation.EnsureFolder);

    /// <summary>
    /// A Drive client that stops inside the first <c>EnsureFolderAsync</c> and stays there until the
    /// test lets it go.
    ///
    /// <para><see cref="FakeDriveClient"/> answers everything from memory, so two callers started
    /// one after the other never actually overlap and a race test over it would pass without
    /// proving anything. This holds the first one open so the second reaches the gate while it is
    /// still held.</para>
    /// </summary>
    private sealed class HeldOpenDriveClient(FakeDriveClient inner) : IDriveClient
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a caller is inside <see cref="EnsureFolderAsync"/>.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public async Task<string> EnsureFolderAsync(
            Guid accountId,
            string folderName,
            string? parentFolderId,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();

            await _released.Task;

            return await inner.EnsureFolderAsync(accountId, folderName, parentFolderId, cancellationToken);
        }

        public Task<DriveResumableSession> BeginResumableUploadAsync(
            Guid accountId,
            DriveUploadRequest request,
            CancellationToken cancellationToken) =>
            inner.BeginResumableUploadAsync(accountId, request, cancellationToken);

        public Task<DriveChunkOutcome> WriteChunkAsync(
            Uri sessionUri,
            Stream content,
            long offset,
            long length,
            long totalSize,
            CancellationToken cancellationToken) =>
            inner.WriteChunkAsync(sessionUri, content, offset, length, totalSize, cancellationToken);

        public Task<long> GetConfirmedLengthAsync(
            Uri sessionUri,
            long totalSize,
            CancellationToken cancellationToken) =>
            inner.GetConfirmedLengthAsync(sessionUri, totalSize, cancellationToken);

        public Task<DriveDownload> OpenDownloadAsync(
            Guid accountId,
            string driveFileId,
            string? rangeHeader,
            CancellationToken cancellationToken) =>
            inner.OpenDownloadAsync(accountId, driveFileId, rangeHeader, cancellationToken);

        public Task MoveAsync(
            Guid accountId,
            string driveFileId,
            string? fromFolderId,
            string toFolderId,
            CancellationToken cancellationToken) =>
            inner.MoveAsync(accountId, driveFileId, fromFolderId, toFolderId, cancellationToken);

        public Task DeleteAsync(Guid accountId, string driveFileId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(accountId, driveFileId, cancellationToken);

        public Task<DriveStorageQuota> GetStorageQuotaAsync(
            Guid accountId,
            CancellationToken cancellationToken) =>
            inner.GetStorageQuotaAsync(accountId, cancellationToken);
    }
}
