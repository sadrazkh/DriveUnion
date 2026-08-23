using System.Security.Claims;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Web.Security;
using FluentAssertions;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// The guard, not the spelling.
///
/// Every assertion here runs a principal built by the real claims factory through the real
/// <c>AuthorizationPolicy</c> the panel is decorated with. Asserting on claim strings would pass
/// just as happily if the policy read a different name — which is exactly the state the product was
/// in before this factory existed: correct claims nobody wrote, correct policies nobody satisfied.
/// </summary>
public class PanelPolicyTests
{
    [Fact]
    public async Task Operator_passes_the_operator_policy_and_not_the_tenant_policy()
    {
        await using var harness = IdentityTestHarness.Create();
        var user = await harness.AddUserAsync("ops@example.com", tenantId: null, isOperator: true);

        var principal = await harness.PrincipalForAsync(user);

        (await harness.SatisfiesAsync(DriveUnionPolicies.Operator, principal)).Should().BeTrue();

        // The pool is the operator's; a tenant's file catalogue is not. Staff with no tenant must be
        // refused by the panel rather than silently scoped to one.
        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Tenant_user_passes_the_tenant_policy_and_not_the_operator_policy()
    {
        await using var harness = IdentityTestHarness.Create();
        var user = await harness.AddUserAsync("customer@example.com", tenantId: Guid.NewGuid());

        var principal = await harness.PrincipalForAsync(user);

        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, principal)).Should().BeTrue();
        (await harness.SatisfiesAsync(DriveUnionPolicies.Operator, principal)).Should().BeFalse();
    }

    [Fact]
    public async Task A_user_with_neither_a_tenant_nor_operator_rights_passes_neither_policy()
    {
        await using var harness = IdentityTestHarness.Create();
        var user = await harness.AddUserAsync("nobody@example.com", tenantId: null, isOperator: false);

        var principal = await harness.PrincipalForAsync(user);

        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, principal)).Should().BeFalse();
        (await harness.SatisfiesAsync(DriveUnionPolicies.Operator, principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Guid_Empty_is_not_a_tenant()
    {
        await using var harness = IdentityTestHarness.Create();
        var user = await harness.AddUserAsync("empty@example.com", tenantId: Guid.Empty);

        var principal = await harness.PrincipalForAsync(user);

        // This is the failure of spec §8, caught at the one place it could enter: a request scoped
        // to Guid.Empty reads an empty database and reports success the whole way down.
        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, principal)).Should().BeFalse();
        principal.GetTenantId().Should().BeNull();

        // And the string is never written at all, so nothing that reads the raw claim can parse it
        // back into a tenant either.
        principal.FindFirstValue(DriveUnionClaimTypes.TenantId).Should().BeNull();
    }

    [Fact]
    public async Task An_operator_row_that_also_carries_a_tenant_id_gets_no_tenant_claim()
    {
        await using var harness = IdentityTestHarness.Create();
        var user = await harness.AddUserAsync("ops@example.com", tenantId: Guid.NewGuid(), isOperator: true);

        var principal = await harness.PrincipalForAsync(user);

        (await harness.SatisfiesAsync(DriveUnionPolicies.Operator, principal)).Should().BeTrue();
        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, principal)).Should().BeFalse();
    }

    [Fact]
    public async Task An_anonymous_principal_passes_neither_policy()
    {
        await using var harness = IdentityTestHarness.Create();

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await harness.SatisfiesAsync(DriveUnionPolicies.Tenant, anonymous)).Should().BeFalse();
        (await harness.SatisfiesAsync(DriveUnionPolicies.Operator, anonymous)).Should().BeFalse();
    }

    /// <summary>
    /// Infrastructure cannot reference Web, so the claim names exist twice. This is the only thing
    /// holding the two copies together, and a rename on either side lands here.
    /// </summary>
    [Fact]
    public void The_factory_and_the_policies_name_the_same_claims()
    {
        DriveUnionClaimsPrincipalFactory.TenantIdClaimType.Should().Be(DriveUnionClaimTypes.TenantId);
        DriveUnionClaimsPrincipalFactory.OperatorClaimType.Should().Be(DriveUnionClaimTypes.Operator);
        DriveUnionClaimsPrincipalFactory.OperatorClaimValue.Should().Be(DriveUnionClaimTypes.OperatorValue);
    }
}
