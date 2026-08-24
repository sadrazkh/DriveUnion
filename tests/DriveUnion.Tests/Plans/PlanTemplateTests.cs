using DriveUnion.Core.Plans;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// A plan is a template whose numbers are copied, not a row anything joins to.
///
/// <para>This is the decision the design says a reviewer should argue with first, so it is the one
/// with the most tests behind it. Every one of them fails the moment somebody "simplifies" an
/// enforcement path into a join.</para>
/// </summary>
public class PlanTemplateTests
{
    [Fact]
    public async Task Editing_a_plan_moves_nobody()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var plans = harness.PlanService();
        await plans.SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up on the standard tier.", null, default);

        var beforeEdit = await plans.GetAsync(tenant.Id, default);
        beforeEdit!.Limits.StorageBytes.Should().Be(PlanCatalogue.Standard.StorageBytes);

        // The operator halves the tier. Nothing on any enforcement path joins to this row, so this
        // has to change nobody — which is the whole point of copying, and the cost that is paid on
        // purpose so that a negotiated override and a per tenant history are expressible at all.
        var row = await harness.Db.Plans.SingleAsync(p => p.Code == PlanCatalogue.StandardCode);
        row.StorageBytes /= 2;
        row.MaxFileBytes /= 2;
        await harness.Db.SaveChangesAsync();

        var afterEdit = await harness.PlanService(context: harness.NewContext()).GetAsync(tenant.Id, default);

        afterEdit!.Limits.Should().Be(
            beforeEdit.Limits,
            "editing a template must change nothing until somebody re-applies it");

        // And re-applying is the whole of "somebody re-applies it": one history row per number that
        // actually moved, and none for the two that did not.
        await plans.SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Re-applied after the tier was re-priced.", null, default);

        var reapplied = await harness.PlanService(context: harness.NewContext()).GetAsync(tenant.Id, default);
        reapplied!.Limits.StorageBytes.Should().Be(PlanCatalogue.Standard.StorageBytes / 2);

        var history = await plans.HistoryAsync(tenant.Id, default);
        var fromReapply = history
            .Where(h => h.Reason.StartsWith("Re-applied", StringComparison.Ordinal))
            .Select(h => h.Field)
            .ToList();

        fromReapply.Should().HaveCount(2);
        fromReapply.Should().Contain(QuotaField.StorageBytes);
        fromReapply.Should().Contain(QuotaField.MaxFileBytes);
    }

    [Fact]
    public async Task Applying_a_plan_writes_one_history_row_for_each_number_that_moved()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var plans = harness.PlanService();

        await plans.SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.BusinessCode, "Negotiated at signing.", null, default);

        var history = await plans.HistoryAsync(tenant.Id, default);

        // The tenant started on the seeded default, so all four numbers move. Order is not asserted:
        // the four rows share one timestamp because they are one act.
        history.Should().HaveCount(4);
        history.Select(h => h.Field).Should().Contain(
            [
                QuotaField.StorageBytes,
                QuotaField.MaxFileBytes,
                QuotaField.MonthlyEgressBytes,
                QuotaField.MaxMembers,
            ]);

        var storage = history.Single(h => h.Field == QuotaField.StorageBytes);
        storage.OldValue.Should().Be(PlanCatalogue.Default.StorageBytes);
        storage.NewValue.Should().Be(PlanCatalogue.Business.StorageBytes);
        storage.PlanCodeAfter.Should().Be(PlanCatalogue.BusinessCode);
        storage.Reason.Should().Be("Negotiated at signing.");
    }

    [Fact]
    public async Task Re_applying_an_unchanged_plan_writes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var plans = harness.PlanService();

        await plans.SetTenantPlanAsync(tenant.Id, PlanCatalogue.StandardCode, "First.", null, default);
        await plans.SetTenantPlanAsync(tenant.Id, PlanCatalogue.StandardCode, "Again.", null, default);

        var history = await plans.HistoryAsync(tenant.Id, default);

        // A page of rows saying «۵۰۰ GB became ۵۰۰ GB» is a page nobody scrolls past to find the
        // change they came for.
        history.Should().OnlyContain(h => h.Reason == "First.");
    }

    [Fact]
    public async Task An_override_moves_one_number_and_leaves_the_tenant_on_its_tier()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var plans = harness.PlanService();

        await plans.SetTenantPlanAsync(tenant.Id, PlanCatalogue.StandardCode, "Standard.", null, default);

        const long negotiated = 3L * 1024 * 1024 * 1024 * 1024;
        var after = await plans.SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, negotiated, "Negotiated 3 TB on a 500 GB tier.", null, default);

        after.Limits.StorageBytes.Should().Be(negotiated);
        after.Limits.MaxFileBytes.Should().Be(
            PlanCatalogue.Standard.MaxFileBytes, "an override moves one number, not four");
        after.PlanCode.Should().Be(
            PlanCatalogue.StandardCode, "an override does not take a customer off their tier");

        // Found by its reason rather than by position: the clock is fixed, so the assignment's four
        // rows and this one share a timestamp and "newest first" cannot separate them.
        var entry = (await plans.HistoryAsync(tenant.Id, default))
            .Single(h => h.Reason.StartsWith("Negotiated", StringComparison.Ordinal));

        entry.PlanCodeBefore.Should().Be(PlanCatalogue.StandardCode);
        entry.PlanCodeAfter.Should().Be(PlanCatalogue.StandardCode);
        entry.NewValue.Should().Be(negotiated);
    }

    [Fact]
    public async Task The_storage_command_M5_named_is_the_same_writer_as_every_other_dimension()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var plans = harness.PlanService();

        await plans.SetTenantStorageQuotaAsync(tenant.Id, 42_000_000_000, "Support raised it.", null, default);

        var history = await plans.HistoryAsync(tenant.Id, default);

        // M5 §10 left exactly one seam. It is still one seam, and it now writes the same audit row
        // the other three dimensions do rather than being a command of its own.
        history.Should().ContainSingle()
            .Which.Field.Should().Be(QuotaField.StorageBytes);
    }

    [Fact]
    public async Task A_retired_plan_keeps_its_tenants_working_and_takes_no_new_ones()
    {
        await using var harness = ServiceTestHarness.Create();
        var staying = harness.SeedTenant("acme");
        var arriving = harness.SeedTenant("globex");
        var plans = harness.PlanService();

        await plans.SetTenantPlanAsync(staying.Id, PlanCatalogue.StandardCode, "On it already.", null, default);

        var row = await harness.Db.Plans.SingleAsync(p => p.Code == PlanCatalogue.StandardCode);
        row.IsRetired = true;
        await harness.Db.SaveChangesAsync();

        var stillThere = await harness.PlanService(context: harness.NewContext()).GetAsync(staying.Id, default);
        stillThere!.Limits.Should().Be(
            PlanCatalogue.Standard, "their numbers are on their own row, so retirement cannot reach them");

        // And it can still be re-applied to the tenant that is on it, which is how an edit reaches
        // somebody the operator has stopped selling to.
        await plans.SetTenantPlanAsync(staying.Id, PlanCatalogue.StandardCode, "Re-applied.", null, default);

        var moving = () => plans.SetTenantPlanAsync(
            arriving.Id, PlanCatalogue.StandardCode, "Sold the retired tier.", null, default);

        await moving.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_default_plan_is_applied_once_and_never_undoes_a_negotiated_number()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var plans = harness.PlanService();

        var first = await plans.ApplyDefaultPlanAsync(tenant.Id, null, default);
        first.PlanCode.Should().Be(PlanCatalogue.DefaultCode);

        const long negotiated = 900L * 1024 * 1024 * 1024;
        await plans.SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, negotiated, "Negotiated.", null, default);

        // Re-running the sign-up default — at start-up, in a migration, by a support script — must
        // not quietly take back what somebody sold.
        var again = await plans.ApplyDefaultPlanAsync(tenant.Id, null, default);

        again.Limits.StorageBytes.Should().Be(negotiated);
    }

    [Fact]
    public async Task A_tenant_nobody_has_touched_is_capped_rather_than_uncapped()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var view = await harness.PlanService().GetAsync(tenant.Id, default);

        // A nullable cap meaning "unlimited" is one migration default away from every tenant being
        // uncapped, and nothing looks wrong until the pool is full. So there is no such value, and
        // the default a row is created with is the smallest tier rather than zero or infinity.
        view!.Limits.Should().Be(PlanCatalogue.Default);
        view.PlanCode.Should().BeNull("carrying a tier's numbers is not the same as being on a tier");
    }

    [Fact]
    public async Task The_seeded_catalogue_is_present_and_says_what_it_is()
    {
        await using var harness = ServiceTestHarness.Create();

        var tiers = await new Infrastructure.Plans.PlanCatalogueReader(harness.Db)
            .ListAsync(includeRetired: true, default);

        tiers.Should().HaveCount(3);
        tiers.Select(t => t.Code).Should().ContainInOrder(
            PlanCatalogue.StarterCode, PlanCatalogue.StandardCode, PlanCatalogue.BusinessCode);

        // Per-file is the error bar on the traffic limit: a tier whose per-file number is a large
        // fraction of its monthly traffic will visibly overshoot and a customer will screenshot it.
        // Half a percent is the placeholder's own promise, and it is the property that has to survive
        // whatever the owner eventually chooses.
        foreach (var tier in tiers)
        {
            (tier.Numbers.MaxFileBytes * 200).Should().BeLessThan(
                tier.Numbers.MonthlyEgressBytes,
                "per-file has to stay far below monthly traffic, or the traffic meter looks broken");
        }

        // And there is no price, deliberately: it is the single column that would turn the catalogue
        // into a billing table, and a price with no engine behind it is a number nobody honours.
        typeof(Plan).GetProperties().Select(p => p.Name)
            .Should().NotContain(name => name.Contains("Price", StringComparison.OrdinalIgnoreCase));
    }
}
