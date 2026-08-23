using DriveUnion.Infrastructure.Seeding;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// The door that mints an administrator, judged without a browser in the way.
///
/// <see cref="FirstRunSetupTests"/> settles what the panel does with it over HTTP; these settle the
/// rule underneath — that the panel gets exactly one operator out of it however hard it is asked.
/// </summary>
public class FirstOperatorTests
{
    private const string OperatorEmail = "owner@example.com";

    // A fixture value. Real ones are chosen by the person at the screen and exist nowhere else.
    private const string GoodPassword = "F1rst!Operator";

    [Fact]
    public async Task An_empty_panel_has_no_operator()
    {
        await using var harness = IdentityTestHarness.Create();

        (await FirstOperator.ExistsAsync(harness.Users)).Should().BeFalse();
    }

    [Fact]
    public async Task Creates_an_operator_with_no_tenant_and_the_password_it_was_given()
    {
        await using var harness = IdentityTestHarness.Create();

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, GoodPassword, IdentityTestHarness.Now);

        result.Outcome.Should().Be(FirstOperatorOutcome.Created);

        var user = await harness.Users.FindByEmailAsync(OperatorEmail);

        user.Should().NotBeNull();
        user!.IsOperator.Should().BeTrue();

        // Operator staff have no tenant. One with a tenant would be handed that customer's whole
        // file catalogue by the claims factory it is about to be run through.
        user.TenantId.Should().BeNull();
        (await harness.Users.CheckPasswordAsync(user, GoodPassword)).Should().BeTrue();

        (await FirstOperator.ExistsAsync(harness.Users)).Should().BeTrue();
    }

    [Fact]
    public async Task The_created_operator_takes_the_one_slot()
    {
        await using var harness = IdentityTestHarness.Create();

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, GoodPassword, IdentityTestHarness.Now);

        // Not decoration: this key is what makes two simultaneous first-run requests a duplicate
        // insert rather than two operators. A random id here would compile, pass every other test in
        // this file, and lose the race guarantee entirely.
        result.User!.Id.Should().Be(FirstOperator.SlotId);
    }

    [Fact]
    public async Task An_operator_that_already_exists_closes_the_door()
    {
        await using var harness = IdentityTestHarness.Create();

        // A random id, the way the configured seeder leaves one: the door is closed by there being
        // an operator, not by the slot being filled.
        await harness.AddUserAsync("seeded@example.com", isOperator: true);

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, GoodPassword, IdentityTestHarness.Now);

        result.Outcome.Should().Be(FirstOperatorOutcome.AlreadyProvisioned);
        result.User.Should().BeNull();

        (await harness.Users.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_seeded_operator_closes_the_door_too()
    {
        await using var harness = IdentityTestHarness.Create(
            ($"{DriveUnionSeedOptions.SectionName}:OperatorEmail", "ops@example.com"),
            ($"{DriveUnionSeedOptions.SectionName}:OperatorPassword", "Op3rator!Pass"));

        // The two doors are independent and this is the only place they meet: configuration seeds at
        // boot, and the setup screen is what happens when it did not.
        await harness.Seeder.SeedAsync();

        (await FirstOperator.ExistsAsync(harness.Users)).Should().BeTrue();

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, GoodPassword, IdentityTestHarness.Now);

        result.Outcome.Should().Be(FirstOperatorOutcome.AlreadyProvisioned);
        (await harness.Users.Users.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The losing half of a race, reconstructed rather than run.
    ///
    /// Two requests that read "no operator" at the same instant both reach the insert, and what
    /// separates them is the database refusing the second row's primary key. That is the state set
    /// up here: the slot is already filled when <c>CreateAsync</c> gets past its own check, exactly
    /// as it would be for the request that arrived a millisecond later.
    ///
    /// It is staged instead of threaded because the suite has no Postgres and a shared SQLite
    /// connection serialises every statement — a real race is not reproducible here, but the
    /// mechanism that decides it is, and it is the mechanism that has to hold.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_the_second_insert_rather_than_this_code_refusing_it()
    {
        await using var harness = IdentityTestHarness.Create();

        // Not an operator, so ExistsAsync says the door is open and the insert is actually attempted
        // — which is the whole point. Only the key collides.
        await harness.AddUserAsync("winner@example.com", id: FirstOperator.SlotId);

        // A second scope, because a second request is one: EF would otherwise refuse the duplicate
        // key from its own change tracker without ever sending a statement, and the constraint under
        // test would go unexercised.
        var result = await FirstOperator.CreateAsync(
            harness.SeparateScopeUsers(), OperatorEmail, GoodPassword, IdentityTestHarness.Now);

        result.Outcome.Should().Be(FirstOperatorOutcome.AlreadyProvisioned);

        // Refused, not crashed, and nothing of the loser's is in the table.
        (await harness.Users.Users.CountAsync()).Should().Be(1);
        (await harness.Users.FindByEmailAsync(OperatorEmail)).Should().BeNull();
    }

    [Fact]
    public async Task A_password_the_policy_refuses_creates_nothing_and_says_what_was_wrong()
    {
        await using var harness = IdentityTestHarness.Create();

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, "tinypw", IdentityTestHarness.Now);

        result.Outcome.Should().Be(FirstOperatorOutcome.Refused);

        // Identity's own words, carried out unchanged. A generic "that did not work" on the first
        // screen of the product is the failure this whole feature exists to remove.
        result.Errors.Should().NotBeEmpty();
        result.Errors.Select(e => e.Description)
            .Should().Contain(d => d.Contains("at least 10 characters", StringComparison.Ordinal));

        (await harness.Users.Users.CountAsync()).Should().Be(0);

        // And the door is still open, because nothing was created.
        (await FirstOperator.ExistsAsync(harness.Users)).Should().BeFalse();
    }

    [Fact]
    public async Task A_refusal_never_carries_the_password_it_refused()
    {
        await using var harness = IdentityTestHarness.Create();

        const string Rejected = "tinypw";

        var result = await FirstOperator.CreateAsync(
            harness.Users, OperatorEmail, Rejected, IdentityTestHarness.Now);

        // Identity's describer quotes a user name back at you; it must never quote a password, and
        // this result is about to be rendered into an HTML page.
        result.Errors.Select(e => e.Description)
            .Should().NotContain(d => d.Contains(Rejected, StringComparison.OrdinalIgnoreCase));
    }
}
