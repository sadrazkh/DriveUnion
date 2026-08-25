using DriveUnion.Core.Application;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The line this product is not allowed to cross.
///
/// There is no global query filter to lean on — deliberately, because one would break /d/{slug} —
/// so isolation is only as good as the <c>tenantId</c> argument on each query. These tests are what
/// stands behind that decision.
/// </summary>
public class TenantIsolationTests
{
    [Fact]
    public async Task Tenant_B_cannot_see_tenant_As_files_in_a_listing()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        harness.SeedFile(a.Id, account.Id, "acme-payroll.zip");

        var catalog = harness.Files();

        (await catalog.ListAsync(a.Id, new FileListFilter(), default)).Should().ContainSingle();
        (await catalog.ListAsync(b.Id, new FileListFilter(), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_As_file_by_its_id()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(a.Id, account.Id);

        var catalog = harness.Files();

        (await catalog.GetAsync(a.Id, file.Id, default)).Should().NotBeNull();

        // Knowing the id is not knowing the file. B gets the same answer it would get for an id
        // that never existed.
        (await catalog.GetAsync(b.Id, file.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_tenant_As_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(a.Id, account.Id);

        var catalog = harness.Files();

        (await catalog.DeleteAsync(b.Id, file.Id, default)).Should().BeFalse();

        // And the refusal is real, not just a false return value.
        var row = await harness.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        row.DeletedAt.Should().BeNull();

        (await catalog.DeleteAsync(a.Id, file.Id, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_file_revokes_the_links_that_pointed_at_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);
        var link = harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        (await harness.Files().DeleteAsync(tenant.Id, file.Id, default)).Should().BeTrue();

        // A live link over a deleted file keeps serving bytes the tenant believes are gone.
        var row = await harness.Db.ShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
        row.IsActive.Should().BeFalse();

        (await harness.PublicLinks().ResolveForDownloadAsync("kx91mzq4", default)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_B_cannot_hang_a_share_link_off_tenant_As_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(a.Id, account.Id);

        var act = () => harness.Links().CreateAsync(
            b.Id, new CreateShareLinkRequest(file.Id, null, null), default);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Tenant_B_cannot_revoke_tenant_As_link()
    {
        await using var harness = ServiceTestHarness.Create();
        var a = harness.SeedTenant("acme");
        var b = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(a.Id, account.Id);
        var link = harness.SeedLink(a.Id, file.Id, "kx91mzq4");

        var links = harness.Links();

        (await links.RevokeAsync(b.Id, link.Id, default)).Should().BeFalse();
        (await links.ListForFileAsync(b.Id, file.Id, default)).Should().BeEmpty();
        (await links.RevokeAsync(a.Id, link.Id, default)).Should().BeTrue();
    }
}
