using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Tests.Services;
using DriveUnion.Web.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// The catalogue as something an operator writes rather than something a seed decides.
///
/// <para>Two properties carry most of these tests. <b>Editing a tier moves nobody</b> — that is the
/// architecture <c>DriveUnionDbContext</c> states in words, and the one an editable catalogue is
/// most likely to break by accident. And <b>a tier goes away by being retired</b>, because
/// <c>Tenant.PlanId</c> is a <c>Restrict</c> foreign key and the alternative is a constraint
/// violation arriving on an operator's screen.</para>
/// </summary>
public class PlanCatalogueEditorTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public async Task A_tier_is_created_and_lands_at_the_end_of_the_list()
    {
        await using var harness = ServiceTestHarness.Create();
        var catalogue = harness.Catalogue();

        var created = await catalogue.CreateAsync(
            new PlanDraft("archive", "بایگانی", new PlanNumbers(4096 * Gib, 16 * Gib, 8192 * Gib, 50)),
            default);

        created.Code.Should().Be("archive");
        created.Numbers.StorageBytes.Should().Be(4096 * Gib);
        created.IsRetired.Should().BeFalse("a tier nobody is on can disturb nobody by being on sale");

        var state = await harness.Catalogue(context: harness.NewContext()).StateAsync(default);

        state.Tiers.Select(t => t.Plan.Code).Should().ContainInOrder(
            PlanCatalogue.StarterCode,
            PlanCatalogue.StandardCode,
            PlanCatalogue.BusinessCode,
            "archive");
    }

    [Fact]
    public async Task A_tier_is_edited_in_place_and_keeps_its_identity()
    {
        await using var harness = ServiceTestHarness.Create();
        var catalogue = harness.Catalogue();

        var before = (await catalogue.UsageAsync(PlanCatalogue.BusinessCode, default))!.Plan;

        var edited = await catalogue.EditAsync(
            PlanCatalogue.BusinessCode,
            new PlanDraft("enterprise", "سازمانی", new PlanNumbers(8192 * Gib, 32 * Gib, 16384 * Gib, 200)),
            default);

        // The row is the same row. A tenant's PlanId points at the id, not the code, so re-coding a
        // tier does not orphan anybody — which is exactly why the default's code is the one that is
        // protected and the rest are not.
        edited.Id.Should().Be(before.Id);
        edited.Code.Should().Be("enterprise");
        edited.Numbers.MaxMembers.Should().Be(200);

        var reread = await harness.Catalogue(context: harness.NewContext())
            .UsageAsync("enterprise", default);

        reread!.Plan.Name.Should().Be("سازمانی");
    }

    /// <summary>
    /// The one that matters. Assigning a plan copies its four numbers onto the workspace's own row
    /// and nothing on any enforcement path joins back, so an edit here has to be invisible to every
    /// customer until somebody re-applies it.
    /// </summary>
    [Fact]
    public async Task Editing_a_tier_leaves_every_workspace_untouched()
    {
        await using var harness = ServiceTestHarness.Create();
        var plans = harness.PlanService();

        var onIt = harness.SeedTenant("acme");
        var alsoOnIt = harness.SeedTenant("globex");
        var elsewhere = harness.SeedTenant("initech");

        await plans.SetTenantPlanAsync(onIt.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantPlanAsync(alsoOnIt.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantPlanAsync(elsewhere.Id, PlanCatalogue.BusinessCode, "Signed up.", null, default);

        var historyBefore = await harness.NewContext().TenantQuotaChanges.AsNoTracking().CountAsync();

        await harness.Catalogue().EditAsync(
            PlanCatalogue.StandardCode,
            new PlanDraft(PlanCatalogue.StandardCode, "استاندارد", new PlanNumbers(1 * Gib, 1 * Gib, 1 * Gib, 1)),
            default);

        (await harness.LimitsAsync(onIt.Id)).Should().Be(
            PlanCatalogue.Standard, "a template edit reaches nobody, which is what makes an override expressible");
        (await harness.LimitsAsync(alsoOnIt.Id)).Should().Be(PlanCatalogue.Standard);
        (await harness.LimitsAsync(elsewhere.Id)).Should().Be(PlanCatalogue.Business);

        // And no history row either: nothing happened to any customer, so nothing is claimed to
        // have. A row here would be an audit trail asserting something untrue.
        (await harness.NewContext().TenantQuotaChanges.AsNoTracking().CountAsync())
            .Should().Be(historyBefore);
    }

    [Fact]
    public async Task The_screen_counts_who_is_on_a_tier_and_who_has_drifted_from_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var plans = harness.PlanService();

        var plain = harness.SeedTenant("acme");
        var negotiated = harness.SeedTenant("globex");

        await plans.SetTenantPlanAsync(plain.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantPlanAsync(negotiated.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantQuotaOverrideAsync(
            negotiated.Id, QuotaField.StorageBytes, 3 * 1024 * Gib, "Negotiated 3 TB.", null, default);

        var usage = await harness.Catalogue(context: harness.NewContext())
            .UsageAsync(PlanCatalogue.StandardCode, default);

        usage!.WorkspacesOnPlan.Should().Be(2);

        // The figure a re-apply confirmation shows: only the drifted one would actually move, and
        // that one moving means somebody's negotiated ceiling being taken back.
        usage.WorkspacesHoldingOtherNumbers.Should().Be(1);
    }

    [Fact]
    public async Task A_tier_is_reordered_by_swapping_with_its_neighbour()
    {
        await using var harness = ServiceTestHarness.Create();
        var catalogue = harness.Catalogue();

        await catalogue.MoveAsync(PlanCatalogue.BusinessCode, PlanMove.Up, default);

        var state = await harness.Catalogue(context: harness.NewContext()).StateAsync(default);
        state.Tiers.Select(t => t.Plan.Code).Should().ContainInOrder(
            PlanCatalogue.StarterCode, PlanCatalogue.BusinessCode, PlanCatalogue.StandardCode);

        // A no-op at the edge rather than a refusal: an arrow that errors at the top of a list is a
        // refusal about nothing, and the screen does not draw one there anyway.
        await harness.Catalogue(context: harness.NewContext())
            .MoveAsync(PlanCatalogue.StarterCode, PlanMove.Up, default);

        var unchanged = await harness.Catalogue(context: harness.NewContext()).StateAsync(default);
        unchanged.Tiers[0].Plan.Code.Should().Be(PlanCatalogue.StarterCode);
    }

    [Fact]
    public async Task Retiring_a_tier_takes_it_off_sale_and_leaves_its_workspaces_alone()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        var retired = await harness.Catalogue().SetRetiredAsync(PlanCatalogue.StandardCode, true, default);
        retired.IsRetired.Should().BeTrue();

        (await harness.LimitsAsync(tenant.Id)).Should().Be(
            PlanCatalogue.Standard, "retirement is about what is sold, not about what anybody holds");

        // Brought back, because retiring the wrong row by one click has to be undoable.
        var restored = await harness.Catalogue(context: harness.NewContext())
            .SetRetiredAsync(PlanCatalogue.StandardCode, false, default);

        restored.IsRetired.Should().BeFalse();
    }

    [Fact]
    public async Task A_tier_a_workspace_is_on_is_refused_rather_than_deleted()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        var deleting = () => harness.Catalogue().DeleteAsync(PlanCatalogue.StandardCode, default);

        // Refused here rather than by the Restrict foreign key, which would reach the screen as a
        // constraint violation. The reason is what the screen turns into a sentence naming retirement.
        (await deleting.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.InUseCannotBeDeleted);

        (await harness.NewContext().Plans.AsNoTracking().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_tier_nobody_is_on_is_deleted()
    {
        await using var harness = ServiceTestHarness.Create();

        await harness.Catalogue().DeleteAsync(PlanCatalogue.BusinessCode, default);

        (await harness.NewContext().Plans.AsNoTracking().AnyAsync(p => p.Code == PlanCatalogue.BusinessCode))
            .Should().BeFalse();
    }

    [Fact]
    public async Task The_tier_the_default_setting_names_cannot_be_retired_deleted_or_recoded()
    {
        await using var harness = ServiceTestHarness.Create();

        var retiring = () => harness.Catalogue().SetRetiredAsync(PlanCatalogue.DefaultCode, true, default);
        var deleting = () => harness.Catalogue().DeleteAsync(PlanCatalogue.DefaultCode, default);
        var recoding = () => harness.Catalogue().EditAsync(
            PlanCatalogue.DefaultCode,
            new PlanDraft("entry", "پایه", PlanCatalogue.Starter),
            default);

        // All three leave Plans:DefaultPlanCode naming nothing, and TenantPlanService answers that
        // with KeyNotFoundException — a 500 at somebody's sign-up is not how an operator should
        // learn they broke it.
        (await retiring.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.DefaultCannotBeRetired);

        (await deleting.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.DefaultCannotBeDeleted);

        (await recoding.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.DefaultCannotBeRecoded);

        // Editing everything except its code is still allowed — the default's numbers are the ones
        // an owner most needs to be able to change.
        var edited = await harness.Catalogue().EditAsync(
            PlanCatalogue.DefaultCode,
            new PlanDraft(PlanCatalogue.DefaultCode, "پایه‌ی تازه", new PlanNumbers(200 * Gib, 2 * Gib, 600 * Gib, 5)),
            default);

        edited.Numbers.StorageBytes.Should().Be(200 * Gib);
    }

    [Fact]
    public async Task A_tier_the_default_setting_does_not_name_is_ordinary()
    {
        await using var harness = ServiceTestHarness.Create();

        // The same command, on a deployment whose default is a different tier. Nothing about
        // «starter» is special except that a setting happens to point at it.
        var catalogue = harness.Catalogue(defaultPlanCode: PlanCatalogue.BusinessCode);

        var retired = await catalogue.SetRetiredAsync(PlanCatalogue.StarterCode, true, default);

        retired.IsRetired.Should().BeTrue();
    }

    [Fact]
    public async Task The_screen_can_tell_that_the_default_setting_names_nothing()
    {
        await using var harness = ServiceTestHarness.Create();

        var state = await harness.Catalogue(defaultPlanCode: "nothing-is-coded-this").StateAsync(default);

        // Start-up deliberately does not check this — it would need a database, and a panel that
        // refuses to boot while the database is briefly away is the worse failure. So the screen
        // carries it, and the operator learns it before a customer does.
        state.DefaultPlanExists.Should().BeFalse();
        state.DefaultPlanCode.Should().Be("nothing-is-coded-this");
    }

    [Fact]
    public async Task A_duplicate_code_is_refused_before_the_unique_index_sees_it()
    {
        await using var harness = ServiceTestHarness.Create();

        var creating = () => harness.Catalogue().CreateAsync(
            new PlanDraft(PlanCatalogue.StandardCode, "دوباره استاندارد", PlanCatalogue.Standard),
            default);

        (await creating.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(
                PlanEditRefusal.CodeTaken,
                "a DbUpdateException naming an index is not a sentence anybody can act on");

        // Including the same code in different letters: it is lower-cased on the way in, so
        // «Starter» and «starter» cannot become two rows the setting picks between by row order.
        var shouting = () => harness.Catalogue().CreateAsync(
            new PlanDraft("  STANDARD  ", "بلند", PlanCatalogue.Standard),
            default);

        (await shouting.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.CodeTaken);
    }

    [Fact]
    public async Task A_tier_keeping_its_own_code_through_an_edit_is_not_a_duplicate_of_itself()
    {
        await using var harness = ServiceTestHarness.Create();

        var saved = await harness.Catalogue().EditAsync(
            PlanCatalogue.StandardCode,
            new PlanDraft(PlanCatalogue.StandardCode, "استاندارد پلاس", PlanCatalogue.Standard),
            default);

        saved.Name.Should().Be("استاندارد پلاس");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Starter")]
    [InlineData("with space")]
    [InlineData("-leading-hyphen")]
    [InlineData("9lives")]
    public async Task A_code_a_configuration_file_could_not_carry_is_refused(string code)
    {
        await using var harness = ServiceTestHarness.Create();

        var creating = () => harness.Catalogue().CreateAsync(
            new PlanDraft(code, "نام", PlanCatalogue.Starter),
            default);

        // «Starter» is in this list because it normalises to «starter», which is taken — either
        // refusal is correct and both are sentences rather than an index error.
        var refusal = (await creating.Should().ThrowAsync<PlanEditRefusedException>()).Which.Reason;

        refusal.Should().BeOneOf(PlanEditRefusal.CodeMalformed, PlanEditRefusal.CodeTaken);
    }

    [Fact]
    public async Task A_ceiling_of_zero_is_refused_because_it_would_refuse_every_upload()
    {
        await using var harness = ServiceTestHarness.Create();

        var creating = () => harness.Catalogue().CreateAsync(
            new PlanDraft("free", "رایگان", new PlanNumbers(0, 0, 0, 0)),
            default);

        (await creating.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.NumberOutOfRange);
    }

    [Fact]
    public async Task A_per_file_ceiling_above_the_storage_cap_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();

        var creating = () => harness.Catalogue().CreateAsync(
            new PlanDraft("odd", "عجیب", new PlanNumbers(10 * Gib, 100 * Gib, 1000 * Gib, 3)),
            default);

        // The storage check refuses such a file first, so the per-file number is a promise the tier
        // could never keep — generous-looking on a customer's card and unreachable in practice.
        (await creating.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.FileLargerThanStorage);
    }

    [Fact]
    public async Task A_ceiling_that_is_not_a_whole_gigabyte_is_refused_so_the_form_cannot_drift()
    {
        await using var harness = ServiceTestHarness.Create();

        var creating = () => harness.Catalogue().CreateAsync(
            new PlanDraft("odd", "عجیب", new PlanNumbers(100 * Gib + 1, 1 * Gib, 300 * Gib, 3)),
            default);

        // One unit in, one unit out. A byte figure that is not a whole number of gigabytes would be
        // rounded by whoever opened the form next, which is a tier changing because somebody looked
        // at it.
        (await creating.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.NumberOutOfRange);
    }

    [Fact]
    public void Units_survive_a_round_trip_through_the_form_at_the_largest_tier()
    {
        var business = new PlanSummary(
            Guid.NewGuid(),
            PlanCatalogue.BusinessCode,
            "تجاری",
            PlanCatalogue.Business,
            IsRetired: false,
            SortOrder: 30);

        var form = PlanForm.From(business);

        // GB means 1024³ here, which is the divisor DisplayFormats already renders with. 6 TiB is
        // 6144 of them — and the field says 6144 rather than the «6 TB» the read-only tables print,
        // which is the whole reason the form does not reuse that formatter.
        form.StorageGb.Should().Be(2048);
        form.MaxFileGb.Should().Be(8);
        form.TrafficGb.Should().Be(6144);
        form.Seats.Should().Be(25);

        form.ToDraft().Numbers.Should().Be(
            PlanCatalogue.Business, "a number that survives the screen has to survive it exactly");
    }

    [Fact]
    public async Task Re_applying_a_tier_moves_the_workspaces_on_it_and_writes_each_a_history_row()
    {
        await using var harness = ServiceTestHarness.Create();
        var plans = harness.PlanService();

        var first = harness.SeedTenant("acme");
        var second = harness.SeedTenant("globex");
        var elsewhere = harness.SeedTenant("initech");

        await plans.SetTenantPlanAsync(first.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantPlanAsync(second.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantPlanAsync(elsewhere.Id, PlanCatalogue.BusinessCode, "Signed up.", null, default);

        var repriced = new PlanNumbers(750 * Gib, 4 * Gib, 2048 * Gib, 15);

        await harness.Catalogue().EditAsync(
            PlanCatalogue.StandardCode,
            new PlanDraft(PlanCatalogue.StandardCode, "استاندارد", repriced),
            default);

        var whoMoved = Guid.CreateVersion7();

        var moved = await harness.Catalogue(context: harness.NewContext()).ReapplyAsync(
            PlanCatalogue.StandardCode, "Re-priced the standard tier.", whoMoved, default);

        moved.Should().Be(2);

        (await harness.LimitsAsync(first.Id)).Should().Be(repriced);
        (await harness.LimitsAsync(second.Id)).Should().Be(repriced);

        // The workspace on another tier is untouched. "Re-apply this tier" is about the tier's own
        // customers and about nobody else's.
        (await harness.LimitsAsync(elsewhere.Id)).Should().Be(PlanCatalogue.Business);

        var history = await harness.NewContext().TenantQuotaChanges.AsNoTracking()
            .Where(c => c.Reason == "Re-priced the standard tier.")
            .ToListAsync();

        // Four numbers moved on each of the two workspaces, and every one of them is a row naming a
        // person, a moment and a reason. A silent bulk move is exactly the question this table was
        // built to answer.
        history.Should().HaveCount(8);
        history.Select(c => c.TenantId).Distinct().Should().BeEquivalentTo(new[] { first.Id, second.Id });
        history.Should().OnlyContain(c => c.ChangedByUserId == whoMoved);
        history.Should().OnlyContain(c => c.PlanCodeAfter == PlanCatalogue.StandardCode);
    }

    [Fact]
    public async Task Re_applying_an_unchanged_tier_moves_nobody_and_writes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        var before = await harness.NewContext().TenantQuotaChanges.AsNoTracking().CountAsync();

        var moved = await harness.Catalogue(context: harness.NewContext()).ReapplyAsync(
            PlanCatalogue.StandardCode, "Nothing changed.", null, default);

        moved.Should().Be(0);

        // A page of rows saying «۵۰۰ GB became ۵۰۰ GB» is a page nobody scrolls past to find the
        // change they came for.
        (await harness.NewContext().TenantQuotaChanges.AsNoTracking().CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Re_applying_takes_back_a_negotiated_ceiling_which_is_why_it_is_its_own_action()
    {
        await using var harness = ServiceTestHarness.Create();
        var plans = harness.PlanService();
        var tenant = harness.SeedTenant("acme");

        await plans.SetTenantPlanAsync(tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);
        await plans.SetTenantQuotaOverrideAsync(
            tenant.Id, QuotaField.StorageBytes, 3 * 1024 * Gib, "Negotiated 3 TB.", null, default);

        await harness.Catalogue(context: harness.NewContext()).ReapplyAsync(
            PlanCatalogue.StandardCode, "Swept the tier.", null, default);

        // This is the expensive mistake the confirmation screen exists to warn about, and it is
        // recorded rather than silent: the row saying 3 TB became 500 GB is what a support
        // conversation reads back to the customer.
        (await harness.LimitsAsync(tenant.Id)).StorageBytes.Should().Be(PlanCatalogue.Standard.StorageBytes);

        var reversal = await harness.NewContext().TenantQuotaChanges.AsNoTracking()
            .SingleAsync(c => c.Reason == "Swept the tier." && c.Field == QuotaField.StorageBytes);

        reversal.OldValue.Should().Be(3 * 1024 * Gib);
        reversal.NewValue.Should().Be(PlanCatalogue.Standard.StorageBytes);
    }

    [Fact]
    public async Task A_retired_tier_can_still_be_re_applied_to_the_workspaces_on_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.PlanService().SetTenantPlanAsync(
            tenant.Id, PlanCatalogue.StandardCode, "Signed up.", null, default);

        var repriced = new PlanNumbers(600 * Gib, 3 * Gib, 1800 * Gib, 12);

        await harness.Catalogue().EditAsync(
            PlanCatalogue.StandardCode,
            new PlanDraft(PlanCatalogue.StandardCode, "استاندارد", repriced),
            default);

        await harness.Catalogue(context: harness.NewContext())
            .SetRetiredAsync(PlanCatalogue.StandardCode, true, default);

        // Retirement stops new assignment; it does not strand the customers already on the tier
        // where an edit can never reach them.
        var moved = await harness.Catalogue(context: harness.NewContext()).ReapplyAsync(
            PlanCatalogue.StandardCode, "Re-priced before it was withdrawn.", null, default);

        moved.Should().Be(1);
        (await harness.LimitsAsync(tenant.Id)).Should().Be(repriced);
    }

    [Fact]
    public async Task A_command_naming_no_tier_is_refused_rather_than_ignored()
    {
        await using var harness = ServiceTestHarness.Create();

        var editing = () => harness.Catalogue().EditAsync(
            "not-a-tier", new PlanDraft("not-a-tier", "نام", PlanCatalogue.Starter), default);

        (await editing.Should().ThrowAsync<PlanEditRefusedException>())
            .Which.Reason.Should().Be(PlanEditRefusal.NotFound);

        (await harness.Catalogue().UsageAsync("not-a-tier", default)).Should().BeNull();
    }
}
