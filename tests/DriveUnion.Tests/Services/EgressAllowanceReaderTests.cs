using DriveUnion.Core.Application;
using DriveUnion.Core.Metering;
using DriveUnion.Infrastructure.Persistence.Repositories;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The two numbers the public download path refuses on: what a workspace has served this calendar
/// month, and what its plan sold it.
///
/// <para>The gate itself is asserted over a live request in <c>PublicEgressCapTests</c>. This is the
/// arithmetic underneath it — the month's edges, whose bytes count, and where «over» begins — and it
/// is here rather than there because each of those is a way to be wrong that a served file cannot
/// tell you about.</para>
/// </summary>
public class EgressAllowanceReaderTests
{
    private const long Allowance = 100_000;

    [Fact]
    public async Task Spending_everything_sold_is_over_and_not_merely_at_the_line()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        SetAllowance(harness, tenant.Id, Allowance);

        Seed(harness, tenant.Id, Today(harness), Allowance);

        var standing = await Reader(harness).ReadAsync(tenant.Id, default);

        standing.SpentBytes.Should().Be(Allowance);
        standing.AllowanceBytes.Should().Be(Allowance);

        // «>=» and not «>», which is the edge TenantPlanView.IsOverStorage already draws: an
        // allowance with exactly nothing left in it has nothing left in it. Off by this one
        // comparison, every workspace in the product gets one extra transfer of whatever size the
        // next visitor asks for — which on this product is measured in hundreds of gigabytes.
        standing.IsOverAllowance.Should().BeTrue();
    }

    [Fact]
    public async Task One_byte_short_is_still_inside_the_allowance()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        SetAllowance(harness, tenant.Id, Allowance);

        Seed(harness, tenant.Id, Today(harness), Allowance - 1);

        // The other side of the same edge. Without it, a reader that refused everybody would pass
        // the test above.
        (await Reader(harness).ReadAsync(tenant.Id, default)).IsOverAllowance.Should().BeFalse();
    }

    [Fact]
    public async Task Last_months_traffic_does_not_refuse_this_months_downloads()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        SetAllowance(harness, tenant.Id, Allowance);

        var today = Today(harness);
        var first = new DateOnly(today.Year, today.Month, 1);

        // Well over the allowance, on the last day of last month and the first of next.
        Seed(harness, tenant.Id, first.AddDays(-1), Allowance * 5);
        Seed(harness, tenant.Id, first.AddMonths(1), Allowance * 5);

        var standing = await Reader(harness).ReadAsync(tenant.Id, default);

        // A window that leaked a day in either direction would black out a customer's links on the
        // first of the month for traffic they were already billed for — the one day of the year they
        // are least likely to believe the panel.
        standing.SpentBytes.Should().Be(0);
        standing.IsOverAllowance.Should().BeFalse();
    }

    [Fact]
    public async Task One_workspace_is_never_refused_for_anothers_bytes()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");

        SetAllowance(harness, mine.Id, Allowance);
        SetAllowance(harness, theirs.Id, Allowance);

        Seed(harness, theirs.Id, Today(harness), Allowance * 3);

        var reader = Reader(harness);

        // The line this product may not cross, restated for a refusal instead of for a bill. There
        // is no signed-in identity on the route this decides, so the wrong predicate here would take
        // a whole product's downloads dark on one busy customer's month.
        (await reader.ReadAsync(mine.Id, default)).IsOverAllowance.Should().BeFalse();
        (await reader.ReadAsync(theirs.Id, default)).IsOverAllowance.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_that_has_served_nothing_is_zero_rather_than_a_failure()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        SetAllowance(harness, tenant.Id, Allowance);

        // No usage row at all, which is every workspace on the first of the month. SUM over no rows
        // is NULL in SQL, and a projection that did not coalesce it throws on the way through SQLite
        // — turning the ordinary case into a 500 on the product's only anonymous route.
        var standing = await Reader(harness).ReadAsync(tenant.Id, default);

        standing.SpentBytes.Should().Be(0);
        standing.IsOverAllowance.Should().BeFalse();
    }

    [Fact]
    public async Task An_allowance_of_zero_serves_nothing_rather_than_everything()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        SetAllowance(harness, tenant.Id, 0);

        // A workspace sold no traffic serves none. Reading zero as «unlimited» — which is what a
        // «> 0 && spent > allowance» guard would do — makes the emptiest row in the table the most
        // generous one in the product, and it is the row a mis-typed override leaves behind.
        (await Reader(harness).ReadAsync(tenant.Id, default)).IsOverAllowance.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_that_is_not_there_is_refused_rather_than_waved_through()
    {
        await using var harness = ServiceTestHarness.Create();

        var standing = await Reader(harness).ReadAsync(Guid.NewGuid(), default);

        // Unreachable behind a live file — StoredFile.TenantId is a foreign key — so this is what
        // happens when something has already gone wrong. Refusing is the direction to be wrong in on
        // a path that spends the operator's bandwidth: the alternative is unmetered egress running
        // until somebody reads a bill.
        standing.Should().Be(new EgressStanding(0, 0));
        standing.IsOverAllowance.Should().BeTrue();
    }

    private static EgressAllowanceReader Reader(ServiceTestHarness harness) =>
        new(harness.Db, harness.Clock);

    private static DateOnly Today(ServiceTestHarness harness) =>
        DateOnly.FromDateTime(harness.Clock.GetUtcNow().UtcDateTime);

    private static void SetAllowance(ServiceTestHarness harness, Guid tenantId, long bytes)
    {
        var tenant = harness.Db.Tenants.Single(t => t.Id == tenantId);
        tenant.MonthlyEgressBytes = bytes;

        harness.Db.SaveChanges();
    }

    private static void Seed(ServiceTestHarness harness, Guid tenantId, DateOnly day, long bytes)
    {
        harness.Db.TenantUsageDays.Add(new TenantUsageDay
        {
            TenantId = tenantId,
            Day = day,
            EgressBytes = bytes,
            Downloads = 1,
        });

        harness.Db.SaveChanges();
    }
}
