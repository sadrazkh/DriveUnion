using System.Reflection;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The resolver is the third anonymous surface in this product, after <c>/d/{slug}</c> and the OAuth
/// callback, and it is the first one where the wrong answer is somebody else's file list rather than
/// a 404. These are the tests that hold that line.
/// </summary>
public class TelegramIdentityReaderTests
{
    [Fact]
    public async Task An_unbound_sender_resolves_to_nobody()
    {
        await using var harness = TelegramTestHarness.Create();

        var tenant = harness.SeedTenant();
        harness.SeedUser(tenant.Id);

        // A Telegram id nobody has ever bound. Anyone in the world can make the bot see one of
        // these, simply by messaging it.
        var identity = await harness.Identities().ResolveAsync(555_000_111, CancellationToken.None);

        identity.Should().BeNull(
            "an unbound sender has no tenant, and null is the only other answer this call has");
    }

    [Fact]
    public async Task A_bound_sender_resolves_to_their_own_tenant_and_role()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(user.Id, 900_100);

        var identity = await harness.Identities().ResolveAsync(900_100, CancellationToken.None);

        identity.Should().NotBeNull();
        identity!.AppUserId.Should().Be(user.Id);
        identity.TenantId.Should().Be(tenant.Id);
        identity.Role.Should().Be(TenantRole.Owner);
    }

    [Fact]
    public async Task Two_bound_senders_never_see_each_others_tenant()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var acme = harness.SeedTenant("acme");
        var globex = harness.SeedTenant("globex");

        var alice = harness.SeedUser(acme.Id);
        var bob = harness.SeedUser(globex.Id);

        await harness.LinkAsync(alice.Id, 111_111);
        await harness.LinkAsync(bob.Id, 222_222);

        var reader = harness.Identities();

        var first = await reader.ResolveAsync(111_111, CancellationToken.None);
        var second = await reader.ResolveAsync(222_222, CancellationToken.None);

        first!.TenantId.Should().Be(acme.Id);
        second!.TenantId.Should().Be(globex.Id);
        first.TenantId.Should().NotBe(second.TenantId);
    }

    [Fact]
    public async Task The_tenant_is_read_live_rather_than_copied_onto_the_mapping()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var acme = harness.SeedTenant("acme");
        var globex = harness.SeedTenant("globex");
        var user = harness.SeedUser(acme.Id);

        await harness.LinkAsync(user.Id, 333_333);

        // The customer moves workspace. Nothing touches TelegramAccount — which is the whole point:
        // a denormalised TenantId on the mapping row would still say "acme" here, silently, and the
        // bot would keep answering out of a tenant this person no longer belongs to.
        var moved = harness.Db.Users.Single(u => u.Id == user.Id);
        moved.TenantId = globex.Id;
        harness.Db.SaveChanges();

        var identity = await harness.Identities().ResolveAsync(333_333, CancellationToken.None);

        identity!.TenantId.Should().Be(globex.Id);
    }

    [Fact]
    public async Task A_binding_whose_user_has_no_tenant_resolves_to_nobody()
    {
        await using var harness = TelegramTestHarness.Create();

        // Operator staff have no tenant. The linking flow refuses them, so this row is written
        // straight to the table — which is exactly the situation the resolver has to survive, since
        // a row that got in by some other route must still not resolve to anything.
        var staff = harness.SeedUser(null, isOperator: true);

        harness.Db.TelegramAccounts.Add(new Core.Telegram.TelegramAccount
        {
            Id = Guid.NewGuid(),
            AppUserId = staff.Id,
            TelegramUserId = 444_444,
            ChatId = 444_444,
            LinkedAt = TelegramTestHarness.Now,
            LastSeenAt = TelegramTestHarness.Now,
        });

        harness.Db.SaveChanges();

        var identity = await harness.Identities().ResolveAsync(444_444, CancellationToken.None);

        identity.Should().BeNull("there is no tenant to answer with, and Guid.Empty is not one");
    }

    [Fact]
    public async Task Unlinking_leaves_nothing_resolvable()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(user.Id, 777_777);

        var before = await harness.Identities().ResolveAsync(777_777, CancellationToken.None);
        before.Should().NotBeNull();

        var outcome = await harness.Links().UnlinkAsync(
            user.Id,
            TelegramUnlinkReason.Customer,
            CancellationToken.None);

        outcome.Unlinked.Should().BeTrue();
        outcome.FarewellChatId.Should().Be(777_777);
        outcome.FarewellText.Should().Be(Core.Telegram.TelegramMessages.Farewell);

        var after = await harness.Identities().ResolveAsync(777_777, CancellationToken.None);

        after.Should().BeNull("the mapping is gone, so the sender is a stranger again");
    }

    [Fact]
    public async Task Removing_the_panel_user_takes_the_binding_with_it()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(user.Id, 888_888);

        // The cascade is a backstop for a direct SQL delete. It is only a backstop — a cascade is
        // silent, and UnlinkAsync exists because the customer has to be told — but the row must not
        // survive the person.
        harness.Db.Users.Remove(harness.Db.Users.Single(u => u.Id == user.Id));
        harness.Db.SaveChanges();

        var identity = await harness.Identities().ResolveAsync(888_888, CancellationToken.None);

        identity.Should().BeNull();
    }

    /// <summary>
    /// The absence of a tenant parameter is load-bearing, so it is asserted rather than trusted to a
    /// comment. A reader that took one would be handed <c>Guid.Empty</c> by its only caller — a
    /// Telegram update has no session to take a tenant from — and would then resolve every bound
    /// customer in the product to nothing while their rows sat plainly in the table.
    /// </summary>
    [Fact]
    public void The_resolver_has_no_way_to_be_handed_a_tenant()
    {
        var methods = typeof(ITelegramIdentityReader).GetMethods();

        methods.Should().HaveCount(1, "one method resolves an identity, and there is no overload");

        methods[0].GetParameters().Select(p => p.ParameterType).Should().Equal(
            [typeof(long), typeof(CancellationToken)],
            "the only inputs are the Telegram sender id and cancellation");

        var surface = typeof(TelegramIdentityReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters())
            .ToList();

        surface.Should().NotContain(
            p => p.ParameterType == typeof(Guid) || p.ParameterType == typeof(Guid?),
            "a Guid on this type would be a tenant or a user id arriving from a caller that has none");

        surface.Should().NotContain(
            p => p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase),
            "nothing sessionless has a tenant to pass");
    }
}
