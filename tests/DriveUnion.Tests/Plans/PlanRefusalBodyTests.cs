using System.Text.Json;
using DriveUnion.Core.Plans;
using DriveUnion.Tests.Services;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.Plans;

/// <summary>
/// What a client is told when a plan refuses, and what it is deliberately not told.
/// </summary>
public class PlanRefusalBodyTests
{
    private static readonly JsonSerializerOptions CamelCase =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_file_refusal_names_the_limit_and_the_two_figures_that_explain_it()
    {
        var body = PlanLimitBodies.For(PlanLimitExceededException.File(3_000_000_000, 1_073_741_824));

        var json = JsonSerializer.SerializeToElement(body, CamelCase);

        json.GetProperty("error").GetString().Should().Be("file_too_large_for_plan");
        json.GetProperty("limit").GetString().Should().Be("file");
        json.GetProperty("maxFileBytes").GetInt64().Should().Be(1_073_741_824);
        json.GetProperty("requestedBytes").GetInt64().Should().Be(3_000_000_000);
    }

    [Fact]
    public void The_storage_refusal_says_how_much_is_spent_and_how_much_there_was()
    {
        var body = PlanLimitBodies.For(
            PlanLimitExceededException.Storage(requestedBytes: 500, usedBytes: 900, capBytes: 1000));

        var json = JsonSerializer.SerializeToElement(body, CamelCase);

        json.GetProperty("error").GetString().Should().Be("tenant_quota_exceeded");
        json.GetProperty("limit").GetString().Should().Be("storage");
        json.GetProperty("capBytes").GetInt64().Should().Be(1000);
        json.GetProperty("usedBytes").GetInt64().Should().Be(900);
        json.GetProperty("requestedBytes").GetInt64().Should().Be(500);
    }

    [Fact]
    public void No_refusal_body_carries_the_exceptions_own_message()
    {
        var refusal = PlanLimitExceededException.Storage(500, 900, 1000);

        var json = JsonSerializer.Serialize(PlanLimitBodies.For(refusal), CamelCase);

        // The same rule the Drive failures follow: a caller gets a code and figures, and the log gets
        // the detail. A message assembled server-side is one refactor away from naming something the
        // customer must not learn.
        json.Should().NotContain(refusal.Message);
    }

    [Fact]
    public void The_four_wire_names_are_the_ones_the_design_spells()
    {
        PlanLimitCodes.For(PlanLimit.Storage).Should().Be("tenant_quota_exceeded");
        PlanLimitCodes.For(PlanLimit.File).Should().Be("file_too_large_for_plan");
        PlanLimitCodes.For(PlanLimit.Traffic).Should().Be("tenant_traffic_exceeded");
        PlanLimitCodes.For(PlanLimit.Members).Should().Be("member_limit_reached");

        PlanLimitCodes.Dimension(PlanLimit.Storage).Should().Be("storage");
        PlanLimitCodes.Dimension(PlanLimit.File).Should().Be("file");
        PlanLimitCodes.Dimension(PlanLimit.Traffic).Should().Be("traffic");
        PlanLimitCodes.Dimension(PlanLimit.Members).Should().Be("members");
    }

    [Fact]
    public async Task A_default_plan_code_that_names_no_tier_fails_loudly_rather_than_silently()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var plans = harness.PlanService(defaultPlanCode: "a-tier-nobody-created");

        var act = () => plans.ApplyDefaultPlanAsync(tenant.Id, null, default);

        // Not a fallback to whatever tier happens to be first. A silent fallback here is how every
        // customer created during a misconfiguration ends up on a plan nobody sold them.
        var thrown = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
        thrown.Message.Should().Contain("a-tier-nobody-created");
    }
}
