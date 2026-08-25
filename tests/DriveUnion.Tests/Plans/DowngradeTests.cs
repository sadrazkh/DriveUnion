using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Uploads;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// A downgrade constrains the next action, never an existing one.
///
/// <para>Three options were on the table and two are not options: deleting the customer's files, and
/// refusing the downgrade — which would make the operator's own commercial action impossible and is
/// the "pretending they fit" the brief warns about. What is left is storing the lower number and
/// letting the tenant be over it, which is a state the product already has.</para>
/// </summary>
public class DowngradeTests
{
    private const int Multiple = UploadChunking.DriveChunkMultiple;

    [Fact]
    public async Task Downgrading_below_current_usage_deletes_nothing_and_refuses_the_next_upload()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await UploadAsync(harness, tenant.Id, "quarterly.mp4", 4 * Multiple);
        await UploadAsync(harness, tenant.Id, "payroll.zip", 4 * Multiple);

        var before = await harness.StorageAsync(tenant.Id);
        before.Used.Should().Be(8 * Multiple);

        // The new cap is under what they already hold.
        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 2 * Multiple, "Moved to the smaller tier.", null, default);

        var after = await harness.StorageAsync(tenant.Id);
        after.Used.Should().Be(8 * Multiple, "already-spent bytes do not change when the ceiling does");
        after.Quota.Should().Be(2 * Multiple);

        (await harness.NewContext().StoredFiles.AsNoTracking().CountAsync(f => f.DeletedAt == null))
            .Should().Be(2, "nothing is deleted, ever, by a pricing change");

        var act = () => harness.Uploads().BeginAsync(
            tenant.Id, ownerUserId: null, new BeginUploadRequest("more.bin", "application/octet-stream", 1024), default);

        var refusal = (await act.Should().ThrowAsync<PlanLimitExceededException>()).Which;
        refusal.Limit.Should().Be(PlanLimit.Storage);
    }

    [Fact]
    public async Task Lowering_the_per_file_limit_leaves_bigger_stored_files_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        await UploadAsync(harness, tenant.Id, "big.mkv", 8 * Multiple);

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.MaxFileBytes, Multiple, "Smaller tier.", null, default);

        // The limit is on the act of uploading, not on possession. Anything else means a pricing
        // change deletes or hides customer data.
        var stored = await harness.NewContext().StoredFiles.AsNoTracking().SingleAsync();
        stored.DeletedAt.Should().BeNull();
        stored.SizeBytes.Should().Be(8 * Multiple);

        var listing = await harness.Files(harness.NewContext()).ListAsync(tenant.Id, nameQuery: null, default);
        listing.Should().ContainSingle();
    }

    [Fact]
    public async Task The_preview_names_the_overage_before_the_operator_confirms()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var plans = harness.PlanService();
        await plans.SetTenantPlanAsync(tenant.Id, PlanCatalogue.BusinessCode, "Business.", null, default);

        // A stored file larger than the starter tier's per-file limit. Seeded rather than uploaded:
        // a test that really moved two gigabytes to prove an arithmetic point is a test nobody runs.
        harness.SeedFile(tenant.Id, account.Id, "master.mov", 2L * 1024 * 1024 * 1024);

        // And a meter just past the starter tier's storage cap.
        await TopUpUsageAsync(harness, tenant.Id, PlanCatalogue.Starter.StorageBytes + 1);

        var preview = await harness.PlanService(context: harness.NewContext())
            .PreviewPlanAsync(tenant.Id, PlanCatalogue.StarterCode, default);

        preview!.ProducesAnOverage.Should().BeTrue();
        preview.StorageOverageBytes.Should().BeGreaterThan(0);

        // The count exists so the honest answer — none of them breaks — is convincing with a number
        // beside it. An operator who downgrades without seeing this hears about it from the customer.
        preview.FilesOverNewFileLimit.Should().Be(1);
        preview.Proposed.Should().Be(PlanCatalogue.Starter);
    }

    [Fact]
    public async Task The_preview_says_so_when_the_workspace_still_fits()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var preview = await harness.PlanService()
            .PreviewPlanAsync(tenant.Id, PlanCatalogue.BusinessCode, default);

        preview!.ProducesAnOverage.Should().BeFalse();
        preview.StorageOverageBytes.Should().Be(0);
        preview.FilesOverNewFileLimit.Should().Be(0);
        preview.MembersOverNewSeatLimit.Should().Be(0);
    }

    /// <summary>A real upload through the coordinator, so the meter holds a number it actually moved.</summary>
    private static async Task UploadAsync(
        ServiceTestHarness harness,
        Guid tenantId,
        string name,
        long sizeBytes)
    {
        var begun = await harness.Uploads().BeginAsync(
            tenantId, ownerUserId: null, new BeginUploadRequest(name, "application/octet-stream", sizeBytes), default);

        // One chunk. The fake Drive checks the body against the declared length, so this is the whole
        // file rather than a convenient prefix.
        using var body = new MemoryStream(new byte[sizeBytes]);
        await harness.Uploads().WriteChunkAsync(tenantId, begun.SessionId, body, 0, sizeBytes, default);
    }

    /// <summary>
    /// Puts the meter where a long-standing customer's would be, without moving a hundred gigabytes
    /// through a test. It goes through the single writer rather than round it, so the arithmetic
    /// under test is the arithmetic that ships.
    /// </summary>
    private static async Task TopUpUsageAsync(ServiceTestHarness harness, Guid tenantId, long bytes)
    {
        var context = harness.NewContext();
        var current = await Infrastructure.Plans.TenantStorageMeter.ReadAsync(context, tenantId, default);

        (await Infrastructure.Plans.TenantStorageMeter.TryReserveAsync(
            context, tenantId, bytes - current.UsedBytes, default))
            .Should().BeTrue();
    }
}
