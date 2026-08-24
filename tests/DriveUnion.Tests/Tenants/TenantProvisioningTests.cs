using DriveUnion.Core.Application;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// The rules behind the operator's two screens, judged against a real database.
///
/// <para>The HTTP tests beside these prove the door is on its hinges. These prove the lock: what is
/// written when a workspace is created, what is refused before anything is written, and — the one
/// that matters most — that every member command is scoped to the tenant it was given and reaches
/// nobody else.</para>
/// </summary>
public class TenantProvisioningTests
{
    [Fact]
    public async Task A_new_workspace_gets_the_plan_it_was_created_with()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateTenantAsync(
            "Acme Bolts", "acme-bolts", PlanCatalogue.StandardCode, null, default);

        result.Refusal.Should().Be(TenantRefusal.None);
        result.Tenant.Should().NotBeNull();

        // The plan's numbers are copied onto the workspace's own row — nothing on an enforcement
        // path joins back to the catalogue, so this is the only place they can be read from.
        result.Tenant!.Limits.Should().Be(PlanCatalogue.Standard);

        var stored = await harness.Db.Tenants
            .AsNoTracking()
            .SingleAsync(t => t.Id == result.Tenant.TenantId);

        stored.StorageQuotaBytes.Should().Be(PlanCatalogue.Standard.StorageBytes);
        stored.MaxMembers.Should().Be(PlanCatalogue.Standard.MaxMembers);
        stored.PlanId.Should().NotBeNull("the operator's screen has to be able to name the tier");

        // The quota history is the answer to «چرا سهمیه‌ام عوض شد», and a workspace whose numbers
        // came from nowhere is the first question that cannot be answered.
        var history = await harness.Plans.HistoryAsync(
            result.Tenant.TenantId, default);

        history.Should().NotBeEmpty();
    }

    /// <summary>
    /// No plan code means <c>Plans:DefaultPlanCode</c>, which is the smallest tier. A workspace with
    /// no plan at all falls back to the column defaults — which works, and is nobody's decision.
    /// </summary>
    [Fact]
    public async Task A_workspace_created_without_a_plan_gets_the_configured_default()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateTenantAsync(
            "Acme", "acme", planCode: null, null, default);

        result.Tenant.Should().NotBeNull();
        result.Tenant!.Limits.Should().Be(PlanCatalogue.Default);
    }

    [Fact]
    public async Task A_duplicate_slug_is_refused_and_writes_nothing()
    {
        await using var harness = TenantServiceHarness.Create();

        await harness.Provisioning.CreateTenantAsync(
            "Acme", "acme", null, null, default);

        var second = await harness.Provisioning.CreateTenantAsync(
            "Acme Again", "ACME  ", null, null, default);

        // Normalised before it is compared, or «ACME» and «acme» are two rows naming one folder
        // inside every Drive account in the pool.
        second.Refusal.Should().Be(TenantRefusal.SlugTaken);
        second.Tenant.Should().BeNull();

        (await harness.Db.Tenants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_malformed_slug_is_refused_before_anything_is_written()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateTenantAsync(
            "Acme Bolts", "Acme Bolts", null, null, default);

        result.Refusal.Should().Be(TenantRefusal.SlugMalformed);
        (await harness.Db.Tenants.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_with_no_name_is_refused()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateTenantAsync(
            "   ", "acme", null, null, default);

        result.Refusal.Should().Be(TenantRefusal.NameRequired);
        (await harness.Db.Tenants.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// A tier that no longer exists must not leave a half-made workspace behind. Both writes are one
    /// transaction, so the refusal is the whole of what happened.
    /// </summary>
    [Fact]
    public async Task A_plan_that_does_not_exist_leaves_no_workspace_behind()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateTenantAsync(
            "Acme", "acme", "no-such-tier", null, default);

        result.Refusal.Should().Be(TenantRefusal.PlanNotFound);
        (await harness.Db.Tenants.AnyAsync()).Should().BeFalse();
    }

    // ── Accounts ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_created_member_belongs_to_the_workspace_and_is_not_an_operator()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var result = await harness.Provisioning.CreateMemberAsync(
            tenant.Id,
            "  Reza@Acme.example  ",
            "  Reza  ",
            TenantServiceHarness.Password,
            default);

        result.Refusal.Should().Be(MemberRefusal.None);

        var user = await harness.Db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == result.UserId);

        user.TenantId.Should().Be(tenant.Id);
        user.Email.Should().Be("Reza@Acme.example", "the address is trimmed and never re-cased");
        user.DisplayName.Should().Be("Reza");

        // The flag with exactly one writer in the codebase, and it is the config seeder. Nothing on
        // this path can set it, because nothing on this path takes it as an argument.
        user.IsOperator.Should().BeFalse();

        // Without this, the disable button silently does nothing: Identity refuses to set a lockout
        // end on a user whose lockout is not enabled.
        user.LockoutEnabled.Should().BeTrue();

        // There is no mail sender in this product and the operator typed both halves by hand — the
        // same reasoning the seeder and the first-run screen use.
        user.EmailConfirmed.Should().BeTrue();
    }

    /// <summary>
    /// <b>The cap refuses the account that would exceed it, before the account exists.</b> This is
    /// the entire reason <c>Tenant.MaxMembers</c> is a column: an account created and then
    /// apologised for is an account that can sign in.
    /// </summary>
    [Fact]
    public async Task The_member_cap_refuses_the_account_that_would_exceed_it()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme", maxMembers: 2);

        foreach (var address in new[] { "one@acme.example", "two@acme.example" })
        {
            var made = await harness.Provisioning.CreateMemberAsync(
                tenant.Id, address, null, TenantServiceHarness.Password, default);

            made.Refusal.Should().Be(MemberRefusal.None);
        }

        var refused = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "three@acme.example", null, TenantServiceHarness.Password, default);

        refused.Refusal.Should().Be(MemberRefusal.SeatsFull);
        refused.SeatsUsed.Should().Be(2);
        refused.MaxMembers.Should().Be(2);

        harness.MemberCount(tenant.Id).Should().Be(2);

        // Not merely "the count did not go up" — the address must not exist at all. A row created
        // and rolled back would still hold the address against Identity's unique index.
        (await harness.Users.FindByEmailAsync("three@acme.example")).Should().BeNull();
    }

    [Fact]
    public async Task An_address_that_is_already_taken_is_refused_in_Identitys_own_words()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        var again = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        again.Refusal.Should().Be(MemberRefusal.IdentityRefused);
        again.Errors.Should().NotBeEmpty("the operator has to learn which of the two fields was wrong");
    }

    [Fact]
    public async Task A_password_below_the_policy_is_refused_and_creates_nobody()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var result = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, "short", default);

        result.Refusal.Should().Be(MemberRefusal.IdentityRefused);
        harness.MemberCount(tenant.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_member_of_a_workspace_that_does_not_exist_is_not_created()
    {
        await using var harness = TenantServiceHarness.Create();

        var result = await harness.Provisioning.CreateMemberAsync(
            Guid.NewGuid(), "reza@acme.example", null, TenantServiceHarness.Password, default);

        result.Refusal.Should().Be(MemberRefusal.TenantNotFound);
        (await harness.Db.Users.AnyAsync()).Should().BeFalse();
    }

    // ── Disabling, re-enabling, and whose account can be reached ──────────────────────────────────

    [Fact]
    public async Task Disabling_locks_the_account_out_and_rebuilds_its_principal()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var created = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        var before = await harness.Users.FindByIdAsync(created.UserId!.Value.ToString());
        var stampBefore = before!.SecurityStamp;

        var disabled = await harness.Provisioning.DisableMemberAsync(
            tenant.Id, created.UserId!.Value, default);

        disabled.Refusal.Should().Be(MemberRefusal.None);
        disabled.Email.Should().Be("reza@acme.example");

        var after = await harness.Db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == created.UserId);

        // Half of "disabled": Identity refuses PasswordSignInAsync while this is in the future.
        after.LockoutEnabled.Should().BeTrue();
        after.LockoutEnd.Should().NotBeNull();
        after.LockoutEnd.Should().BeAfter(TenantServiceHarness.Now);

        // The other half, and the one that reaches somebody who is already signed in: the cookie
        // carries the stamp it was minted with, and SecurityStampValidator rejects the principal
        // when the row disagrees. AddDriveUnionTenancy makes that comparison happen every request.
        after.SecurityStamp.Should().NotBe(stampBefore);

        var view = await harness.Directory.GetAsync(tenant.Id, default);
        view!.Members.Single().IsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task Re_enabling_clears_the_lockout_and_the_failed_attempt_count()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var created = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        await harness.Provisioning.DisableMemberAsync(
            tenant.Id, created.UserId!.Value, default);

        var enabled = await harness.Provisioning.EnableMemberAsync(
            tenant.Id, created.UserId!.Value, default);

        enabled.Refusal.Should().Be(MemberRefusal.None);

        var after = await harness.Db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == created.UserId);

        after.LockoutEnd.Should().BeNull();
        after.AccessFailedCount.Should().Be(0);

        var view = await harness.Directory.GetAsync(tenant.Id, default);
        view!.Members.Single().IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_reset_password_replaces_the_hash_and_ends_the_sessions_the_old_one_had_open()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var created = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        var before = await harness.Users.FindByIdAsync(created.UserId!.Value.ToString());
        var stampBefore = before!.SecurityStamp;

        var reset = await harness.Provisioning.ResetMemberPasswordAsync(
            tenant.Id, created.UserId!.Value, "Another-Horse-7!", default);

        reset.Refusal.Should().Be(MemberRefusal.None);

        var after = await harness.Users.FindByIdAsync(created.UserId!.Value.ToString());

        (await harness.Users.CheckPasswordAsync(after!, "Another-Horse-7!")).Should().BeTrue();
        (await harness.Users.CheckPasswordAsync(after!, TenantServiceHarness.Password)).Should().BeFalse();

        // The reason this matters is the reason for most resets: somebody else knows the old one.
        after!.SecurityStamp.Should().NotBe(stampBefore);
    }

    [Fact]
    public async Task A_refused_new_password_leaves_the_old_one_working()
    {
        await using var harness = TenantServiceHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var created = await harness.Provisioning.CreateMemberAsync(
            tenant.Id, "reza@acme.example", null, TenantServiceHarness.Password, default);

        var reset = await harness.Provisioning.ResetMemberPasswordAsync(
            tenant.Id, created.UserId!.Value, "short", default);

        reset.Refusal.Should().Be(MemberRefusal.IdentityRefused);

        // The alternative implementation — remove the password, then add the new one — leaves the
        // account with no password at all when the second half is refused.
        var after = await harness.Users.FindByIdAsync(created.UserId!.Value.ToString());
        (await harness.Users.CheckPasswordAsync(after!, TenantServiceHarness.Password)).Should().BeTrue();
    }

    /// <summary>
    /// <b>Every member command is scoped to the tenant it was given.</b> A workspace's id and a user
    /// id from a different workspace do not combine into a command; neither does an operator's own
    /// id, whose <c>TenantId</c> is null — which is why this screen cannot lock the operator out of
    /// the panel it lives in.
    /// </summary>
    [Fact]
    public async Task A_member_command_reaches_nobody_outside_the_workspace_it_names()
    {
        await using var harness = TenantServiceHarness.Create();

        var a = harness.SeedTenant("alpha");
        var b = harness.SeedTenant("beta");

        var inB = await harness.Provisioning.CreateMemberAsync(
            b.Id, "b@beta.example", null, TenantServiceHarness.Password, default);

        var operatorAccount = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = "ops@driveunion.test",
            Email = "ops@driveunion.test",
            EmailConfirmed = true,
            IsOperator = true,
            TenantId = null,
            LockoutEnabled = true,
            CreatedAt = TenantServiceHarness.Now,
        };

        (await harness.Users.CreateAsync(operatorAccount, TenantServiceHarness.Password))
            .Succeeded.Should().BeTrue();

        foreach (var stranger in new[] { inB.UserId!.Value, operatorAccount.Id })
        {
            (await harness.Provisioning.DisableMemberAsync(a.Id, stranger, default))
                .Refusal.Should().Be(MemberRefusal.MemberNotFound);

            (await harness.Provisioning.EnableMemberAsync(a.Id, stranger, default))
                .Refusal.Should().Be(MemberRefusal.MemberNotFound);

            (await harness.Provisioning.ResetMemberPasswordAsync(
                a.Id, stranger, "Another-Horse-7!", default))
                .Refusal.Should().Be(MemberRefusal.MemberNotFound);
        }

        // Nothing moved on either of them.
        var stillInB = await harness.Db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == inB.UserId);

        stillInB.LockoutEnd.Should().BeNull();

        var stillOperating = await harness.Db.Users
            .AsNoTracking()
            .SingleAsync(u => u.Id == operatorAccount.Id);

        stillOperating.LockoutEnd.Should().BeNull();
        (await harness.Users.CheckPasswordAsync(stillOperating, TenantServiceHarness.Password))
            .Should().BeTrue();
    }

    /// <summary>
    /// The list is the operator's home page for this part of the product, and every figure on it has
    /// to be about the workspace on that row.
    /// </summary>
    [Fact]
    public async Task The_list_reports_each_workspaces_own_slug_plan_seats_and_usage()
    {
        await using var harness = TenantServiceHarness.Create();

        var alpha = await harness.Provisioning.CreateTenantAsync(
            "Alpha", "alpha", PlanCatalogue.StarterCode, null, default);

        await harness.Provisioning.CreateTenantAsync(
            "Beta", "beta", PlanCatalogue.BusinessCode, null, default);

        await harness.Provisioning.CreateMemberAsync(
            alpha.Tenant!.TenantId,
            "a@alpha.example",
            null,
            TenantServiceHarness.Password,
            default);

        var rows = await harness.Directory.ListAsync(default);

        rows.Should().HaveCount(2);

        var listed = rows.Single(r => r.Slug == "alpha");

        listed.Name.Should().Be("Alpha");
        listed.PlanCode.Should().Be(PlanCatalogue.StarterCode);
        listed.MemberCount.Should().Be(1);
        listed.MaxMembers.Should().Be(PlanCatalogue.Starter.MaxMembers);
        listed.StorageQuotaBytes.Should().Be(PlanCatalogue.Starter.StorageBytes);
        listed.FileCount.Should().Be(0);

        rows.Single(r => r.Slug == "beta").MemberCount.Should().Be(0);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_reads_as_null_rather_than_as_an_empty_one()
    {
        await using var harness = TenantServiceHarness.Create();

        (await harness.Directory.GetAsync(Guid.NewGuid(), default))
            .Should().BeNull();
    }
}
