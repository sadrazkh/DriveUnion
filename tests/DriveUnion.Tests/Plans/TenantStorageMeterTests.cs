using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The single writer of <c>Tenant.StorageUsedBytes</c>, tested against a real relational database
/// because every promise it makes is a promise about one SQL statement.
/// </summary>
public class TenantStorageMeterTests
{
    [Fact]
    public async Task A_reservation_that_does_not_fit_takes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 1000, "Tiny.", null, default);

        (await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 400, default)).Should().BeTrue();
        (await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 700, default)).Should().BeFalse();

        // The test and the increment are the same statement, so a refusal cannot have moved the
        // counter part of the way. Check-then-act in C# loses this and the loss is invisible until
        // two uploads overlap.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(400);
    }

    [Fact]
    public async Task Two_callers_holding_their_own_snapshots_cannot_spend_the_same_bytes()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 1000, "Room for one.", null, default);

        // Two contexts over one database, each with its own change tracker — the shape of two
        // requests that both read "there is room" a moment apart.
        var first = harness.NewContext();
        var second = harness.NewContext();

        var a = await TenantStorageMeter.TryReserveAsync(first, tenant.Id, 600, default);
        var b = await TenantStorageMeter.TryReserveAsync(second, tenant.Id, 600, default);

        new[] { a, b }.Should().ContainSingle(taken => taken, "the database decides, and it can only decide once");
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(600);
    }

    [Fact]
    public async Task Settling_smaller_than_reserved_gives_the_difference_back()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 1000, default);
        await TenantStorageMeter.SettleAsync(harness.Db, tenant.Id, reservedBytes: 1000, actualBytes: 600, default);

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(600);
    }

    [Fact]
    public async Task Settling_larger_than_reserved_keeps_the_difference_even_past_the_cap()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 1000, "Tight.", null, default);

        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 900, default);
        await TenantStorageMeter.SettleAsync(harness.Db, tenant.Id, reservedBytes: 900, actualBytes: 1400, default);

        // A session that already reserved finishes even when it carries the tenant past the cap:
        // killing a 90%-complete 200 GB upload is a worse outcome than a temporary overage, and the
        // cap blocks the next upload instead. The bytes really are there, so the counter says so.
        var storage = await harness.StorageAsync(tenant.Id);
        storage.Used.Should().Be(1400);
        storage.Used.Should().BeGreaterThan(storage.Quota);

        (await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 1, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_release_cannot_drive_the_counter_negative()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 500, default);

        await TenantStorageMeter.ReleaseAsync(harness.Db, tenant.Id, 500, default);
        await TenantStorageMeter.ReleaseAsync(harness.Db, tenant.Id, 500, default);

        // The floor is in the WHERE, so it is applied by the same statement as the subtraction. A
        // negative counter is free storage for as long as it takes somebody to notice.
        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(0);
    }

    [Fact]
    public async Task A_reservation_does_not_leave_a_stale_counter_behind_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        // The row is loaded and tracked first, which is what a request that read the tenant before
        // reserving would have done.
        var tracked = await harness.Db.Tenants.FindAsync(tenant.Id);
        tracked!.StorageUsedBytes.Should().Be(0);

        await TenantStorageMeter.TryReserveAsync(harness.Db, tenant.Id, 700, default);

        // ExecuteUpdate goes round the change tracker, so that copy still holds a zero. Left
        // attached, the next SaveChanges in this scope — the upload session row this very
        // reservation was taken for — could write it back over the move.
        harness.Db.ChangeTracker.Entries<Core.Tenancy.Tenant>().Should().BeEmpty(
            "the moved row is detached rather than left holding a number that is no longer true");

        await harness.Db.SaveChangesAsync();

        (await harness.StorageAsync(tenant.Id)).Used.Should().Be(700);
    }
}
