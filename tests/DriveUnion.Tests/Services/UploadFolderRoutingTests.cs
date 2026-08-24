using DriveUnion.Core.Application;
using DriveUnion.Tests.Fakes;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Which folder an upload actually opens its Drive session in.
///
/// <para>Read through the fake's own session rather than through anything the coordinator returns:
/// the parent on <c>DriveUploadRequest</c> is what Google will put the bytes under, and it is the
/// only statement about the file's location that is not a claim.</para>
/// </summary>
public class UploadFolderRoutingTests
{
    [Fact]
    public async Task An_upload_lands_in_the_uploaders_own_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var maryam = Guid.NewGuid();

        await harness.Uploads().BeginAsync(
            tenant.Id, maryam, new BeginUploadRequest("quarterly.mp4", "video/mp4", 1024), default);

        var root = harness.Drive.Folders.Single(f => f.Name == "DriveUnion");
        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        tenantFolder.ParentFolderId.Should().Be(root.Id);

        var home = harness.Drive.Folders.Single(f => f.Name == $"u-{maryam:N}");
        home.ParentFolderId.Should().Be(tenantFolder.Id);

        var session = harness.Drive.Sessions.Values.Single();
        session.Request.ParentFolderId.Should().Be(
            home.Id, "the bytes go where the resolver said, not where this method could work out");
        session.AccountId.Should().Be(account.Id);
    }

    [Fact]
    public async Task Two_people_in_one_tenant_upload_into_two_folders()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();
        var maryam = Guid.NewGuid();
        var reza = Guid.NewGuid();

        await harness.Uploads().BeginAsync(
            tenant.Id, maryam, new BeginUploadRequest("payroll.zip", "application/zip", 1024), default);

        await harness.Uploads().BeginAsync(
            tenant.Id, reza, new BeginUploadRequest("payroll.zip", "application/zip", 1024), default);

        var hers = harness.Drive.Folders.Single(f => f.Name == $"u-{maryam:N}");
        var his = harness.Drive.Folders.Single(f => f.Name == $"u-{reza:N}");

        hers.Id.Should().NotBe(his.Id);

        // Same tenant, same file name, same account — and two different parents. Neither session can
        // write into the other person's folder, because neither was ever told the other's id.
        var parents = harness.Drive.Sessions.Values.Select(s => s.Request.ParentFolderId).ToList();
        parents.Should().BeEquivalentTo([hers.Id, his.Id]);

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");
        hers.ParentFolderId.Should().Be(tenantFolder.Id);
        his.ParentFolderId.Should().Be(tenantFolder.Id);
    }

    [Fact]
    public async Task The_same_person_uploading_twice_resolves_the_folder_once()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();
        var maryam = Guid.NewGuid();

        await harness.Uploads().BeginAsync(
            tenant.Id, maryam, new BeginUploadRequest("first.bin", "application/octet-stream", 1024), default);

        // DriveUnion, acme, and her folder. Once each, and never again in this process.
        EnsureFolderCalls(harness).Should().Be(3);

        // A second coordinator over a second context, which is what the next request is. What they
        // have in common is the cache.
        await harness.Uploads(harness.NewContext()).BeginAsync(
            tenant.Id, maryam, new BeginUploadRequest("second.bin", "application/octet-stream", 1024), default);

        EnsureFolderCalls(harness).Should().Be(
            3, "the request the cache exists for is the one it does not make");

        var parents = harness.Drive.Sessions.Values.Select(s => s.Request.ParentFolderId).ToList();
        parents.Should().HaveCount(2);
        parents.Distinct().Should().ContainSingle("both uploads went into the one folder she has");
    }

    [Fact]
    public async Task An_upload_with_no_owner_lands_in_the_tenant_folder()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        // The interface's own signature, which is what every caller in the product reaches today.
        await harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("quarterly.mp4", "video/mp4", 1024), default);

        var tenantFolder = harness.Drive.Folders.Single(f => f.Name == "acme");

        harness.Drive.Sessions.Values.Single().Request.ParentFolderId.Should().Be(tenantFolder.Id);

        harness.Drive.Folders.Should().NotContain(
            f => f.Name.StartsWith("u-", StringComparison.Ordinal),
            "files sat in the tenant folder before this existed, and a caller with no person still "
            + "puts them there");
    }

    [Fact]
    public async Task A_tenants_folder_is_shared_by_its_people_and_resolved_once()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await harness.Uploads().BeginAsync(
            tenant.Id, Guid.NewGuid(), new BeginUploadRequest("a.bin", "application/octet-stream", 1), default);

        await harness.Uploads().BeginAsync(
            tenant.Id, Guid.NewGuid(), new BeginUploadRequest("b.bin", "application/octet-stream", 1), default);

        // Four, not six: the second person's upload paid for their own folder and nothing above it.
        EnsureFolderCalls(harness).Should().Be(4);
        harness.Drive.Folders.Count(f => f.Name == "acme").Should().Be(1);
    }

    private static int EnsureFolderCalls(ServiceTestHarness harness) =>
        harness.Drive.Calls.Count(c => c.Operation == FakeDriveOperation.EnsureFolder);
}
