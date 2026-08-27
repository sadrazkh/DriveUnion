using DriveUnion.Core.Application;
using DriveUnion.Core.Notifications;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// The device table, and — most of this file — the three ways a row leaves it.
///
/// <para><b>Pruning is the half of this feature that has no symptom.</b> A subscription is per
/// device and endpoints die: a browser profile is cleared, a phone is replaced, a push service
/// expires the address on its own schedule and tells nobody. Every one of those leaves a row that
/// costs a request, a socket and a timeout on every notification for the life of the deployment,
/// and the only thing anybody sees is that notifications feel slow — which nobody reports. So there
/// are three ways out and all three are tested here: the push service says the endpoint is gone, the
/// sends keep failing, and nothing has heard from the device in three months.</para>
/// </summary>
public class PushSubscriptionTests
{
    // ── registering ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_is_registered_against_the_person_who_was_signed_in()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        var device = await harness.SeedDeviceAsync(user);

        device.UserId.Should().Be(user.Id);
        device.TenantId.Should().Be(tenant.Id);
        device.ConsecutiveFailures.Should().Be(0);
        device.CreatedAt.Should().Be(ServiceTestHarness.Now);
        device.LastSeenAt.Should().Be(ServiceTestHarness.Now);
    }

    /// <summary>
    /// <b>Subscribing twice from one browser is one row.</b>
    ///
    /// <para>A browser hands back the same endpoint every time it is asked, so an insert would give
    /// one phone two mailboxes and every notification would arrive on it twice. The unique index on
    /// the endpoint is what makes that impossible; this is what says the store expects it.</para>
    /// </summary>
    [Fact]
    public async Task Subscribing_again_from_the_same_device_refreshes_the_row_it_already_has()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        const string endpoint = "https://push.example.test/one";

        var first = await harness.Subscriptions().SaveAsync(
            tenant.Id, user.Id, endpoint, p256dh, auth, "fa", default);

        harness.Clock.Advance(TimeSpan.FromDays(3));

        var second = await harness.Subscriptions().SaveAsync(
            tenant.Id, user.Id, endpoint, p256dh, auth, "en", default);

        second.Id.Should().Be(first.Id);

        var db = harness.NewContext();
        var rows = await db.PushSubscriptions.ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Culture.Should().Be("en", "the device says which language it is being read in now");
        rows[0].LastSeenAt.Should().Be(ServiceTestHarness.Now.AddDays(3));
    }

    /// <summary>
    /// A shared computer: the device becomes whoever is signed in now, rather than notifying both.
    ///
    /// <para>The endpoint belongs to the browser profile, not to the account. Keyed on
    /// <c>(user, endpoint)</c> this would be two rows and the previous person's notifications would
    /// keep arriving on a browser somebody else is now using.</para>
    /// </summary>
    [Fact]
    public async Task A_device_signed_into_by_somebody_else_stops_being_the_first_persons()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var first = harness.SeedUser(tenant.Id);
        var second = harness.SeedUser(tenant.Id);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        const string endpoint = "https://push.example.test/shared";

        await harness.Subscriptions().SaveAsync(tenant.Id, first.Id, endpoint, p256dh, auth, "fa", default);
        await harness.Subscriptions().SaveAsync(tenant.Id, second.Id, endpoint, p256dh, auth, "fa", default);

        var db = harness.NewContext();

        (await db.PushSubscriptions.ToListAsync()).Should().ContainSingle()
            .Which.UserId.Should().Be(second.Id);
    }

    /// <summary>
    /// Anything that is not the shape a browser produces is refused rather than stored.
    ///
    /// <para>These arrive in a POST body. A stored row that cannot be encrypted to is a row that
    /// fails five times and is then deleted — five sends nobody could ever have made — and an
    /// endpoint with a scheme of the caller's choosing is this server dialling an address somebody
    /// named, which is the one thing <c>RemoteAddressPolicy</c> exists to prevent on the other path
    /// where that is possible.</para>
    /// </summary>
    [Theory]
    [InlineData("http://push.example.test/insecure", true, true)]
    [InlineData("ftp://push.example.test/x", true, true)]
    [InlineData("not a url", true, true)]
    [InlineData("https://push.example.test/ok", false, true)]
    [InlineData("https://push.example.test/ok", true, false)]
    public async Task A_subscription_that_is_not_the_shape_a_browser_produces_is_refused(
        string endpoint,
        bool realKey,
        bool realAuth)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        var saved = await harness.Subscriptions().SaveAsync(
            tenant.Id,
            user.Id,
            endpoint,
            realKey ? p256dh : "bm90LWEta2V5",
            realAuth ? auth : "c2hvcnQ",
            "fa",
            default);

        saved.Refusal.Should().Be(PushSubscriptionRefusal.Malformed);

        var db = harness.NewContext();
        (await db.PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// One person cannot register an unbounded number of devices.
    ///
    /// <para>Nobody has twenty; a browser that re-subscribes under a new endpoint every time its
    /// profile is cleared does. Without the cap that is a table growing for ever with rows that will
    /// each be tried five times before they are removed.</para>
    /// </summary>
    [Fact]
    public async Task A_person_cannot_register_more_devices_than_the_cap()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        for (var i = 0; i < PushSubscription.MostPerUser; i++) await harness.SeedDeviceAsync(user);

        var (p256dh, auth) = PushTestSupport.DeviceKeys();

        var refused = await harness.Subscriptions().SaveAsync(
            tenant.Id, user.Id, "https://push.example.test/one-too-many", p256dh, auth, "fa", default);

        refused.Refusal.Should().Be(PushSubscriptionRefusal.TooMany);

        (await harness.Subscriptions().CountForUserAsync(user.Id, default))
            .Should().Be(PushSubscription.MostPerUser);
    }

    // ── who an event is for ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A workspace's devices are that workspace's, and one person's are that person's.
    ///
    /// <para>The isolation rule, in the one slice where getting it wrong means a customer's news on
    /// a stranger's phone. There is no global query filter in this model and there must not be one —
    /// <c>/d/{slug}</c> is anonymous and a filter would resolve to <c>Guid.Empty</c> — so every read
    /// here says whose it is, and this is what proves the argument is being used.</para>
    /// </summary>
    [Fact]
    public async Task An_audience_reaches_only_the_devices_it_names()
    {
        await using var harness = ServiceTestHarness.Create();
        var acme = harness.SeedTenant("acme");
        var other = harness.SeedTenant("other");

        var reza = harness.SeedUser(acme.Id);
        var sara = harness.SeedUser(acme.Id);
        var stranger = harness.SeedUser(other.Id);

        var rezasPhone = await harness.SeedDeviceAsync(reza);
        var rezasLaptop = await harness.SeedDeviceAsync(reza);
        var sarasPhone = await harness.SeedDeviceAsync(sara);
        await harness.SeedDeviceAsync(stranger);

        var store = harness.Subscriptions(harness.NewContext());

        var workspace = await store.ForAsync(PushAudience.Workspace(acme.Id), default);

        workspace.Select(s => s.Id).Should().BeEquivalentTo(
            [rezasPhone.Id, rezasLaptop.Id, sarasPhone.Id],
            "a queued deletion is the workspace's news and nobody in particular asked for it");

        var person = await store.ForAsync(PushAudience.Person(acme.Id, reza.Id), default);

        person.Select(s => s.Id).Should().BeEquivalentTo(
            [rezasPhone.Id, rezasLaptop.Id],
            "both of this person's devices, and neither of anybody else's");
    }

    /// <summary>
    /// <b>The operator audience is a question about user rows, not a flag on the device.</b>
    ///
    /// <para>Whether somebody is operator staff is a fact that changes — it is on <c>AppUser</c> and
    /// it is what the panel's own policy authorises on. A copy of it on the subscription would keep
    /// notifying a former colleague about abuse reports until somebody remembered to go and update
    /// their devices, which nobody would.</para>
    /// </summary>
    [Fact]
    public async Task The_operator_audience_follows_the_user_row_and_not_a_copy_on_the_device()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var staff = harness.SeedUser(tenantId: null, isOperator: true);
        var customer = harness.SeedUser(tenant.Id);

        var staffPhone = await harness.SeedDeviceAsync(staff);
        await harness.SeedDeviceAsync(customer);

        var store = harness.Subscriptions(harness.NewContext());

        (await store.ForAsync(PushAudience.OperatorStaff, default))
            .Select(s => s.Id).Should().Equal([staffPhone.Id]);

        // They leave. The row is untouched and the audience answer changes on the next report.
        var db = harness.NewContext();
        var demoted = await db.Users.SingleAsync(u => u.Id == staff.Id);
        demoted.IsOperator = false;
        await db.SaveChangesAsync();

        (await harness.Subscriptions(harness.NewContext()).ForAsync(PushAudience.OperatorStaff, default))
            .Should().BeEmpty("an abuse report must not reach somebody who is no longer staff");
    }

    // ── pruning ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A 404 or a 410 deletes the row at once.</b>
    ///
    /// <para>The one unambiguous answer in this protocol: the push service is saying the endpoint
    /// does not exist. There is nothing to retry and nothing to count.</para>
    /// </summary>
    [Fact]
    public async Task An_endpoint_the_push_service_says_is_gone_is_deleted_at_once()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var device = await harness.SeedDeviceAsync(user);

        await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Gone("410"), default);

        var db = harness.NewContext();
        (await db.PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// <b>Repeated failure ends a subscription; one failure does not.</b>
    ///
    /// <para>The first four are a push service having a bad afternoon, which must not cost a customer
    /// their notifications. The fifth is an endpoint that has stopped working, and keeping it is a
    /// queue that never drains — every notification for the life of the deployment carrying the cost
    /// of a device that was thrown away.</para>
    /// </summary>
    [Fact]
    public async Task A_run_of_failures_ends_a_subscription_and_a_single_one_does_not()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var device = await harness.SeedDeviceAsync(user);

        for (var attempt = 1; attempt < PushSubscription.MaxConsecutiveFailures; attempt++)
        {
            await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);

            var surviving = await harness.NewContext().PushSubscriptions.SingleAsync();

            surviving.ConsecutiveFailures.Should().Be(attempt);
            surviving.LastFailureReason.Should().Be("500");
        }

        await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);

        (await harness.NewContext().PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// One success clears the count.
    ///
    /// <para>Otherwise the counter is «failures ever», and a device that has been reachable for a
    /// year is removed because a push service had five bad days across it.</para>
    /// </summary>
    [Fact]
    public async Task A_delivery_that_lands_forgives_the_failures_before_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var device = await harness.SeedDeviceAsync(user);

        for (var i = 0; i < PushSubscription.MaxConsecutiveFailures - 1; i++)
        {
            await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);
        }

        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Accepted, default);

        var refreshed = await harness.NewContext().PushSubscriptions.SingleAsync();

        refreshed.ConsecutiveFailures.Should().Be(0);
        refreshed.LastFailureReason.Should().BeNull();
        refreshed.LastSeenAt.Should().Be(ServiceTestHarness.Now.AddHours(1));

        // …and the counter really is starting again rather than being one below the ceiling.
        for (var i = 0; i < PushSubscription.MaxConsecutiveFailures - 1; i++)
        {
            await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);
        }

        (await harness.NewContext().PushSubscriptions.ToListAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// <b>A device nothing has heard from in three months is swept.</b>
    ///
    /// <para>The failure counter cannot reach this one: an endpoint that is never sent to is never
    /// found to be dead, so a workspace with no fetches and no deletions accumulates rows for ever.
    /// </para>
    ///
    /// <para>The comparison is in memory on purpose. SQLite will not compare a
    /// <c>DateTimeOffset</c> in SQL, these tests run on SQLite and production is Postgres — a
    /// <c>WHERE</c> over that column would behave differently in the two places, which is a sweep
    /// that works in the test suite and deletes nothing, or everything, in production.</para>
    /// </summary>
    [Fact]
    public async Task A_device_nothing_has_heard_from_for_three_months_is_swept()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        var forgotten = await harness.SeedDeviceAsync(user);

        harness.Clock.Advance(PushSubscription.StaleAfter - TimeSpan.FromDays(1));

        var live = await harness.SeedDeviceAsync(user);

        // A day past the window for the first, a day short of it for the second.
        harness.Clock.Advance(TimeSpan.FromDays(2));

        (await harness.Subscriptions().SweepStaleAsync(default)).Should().Be(1);

        (await harness.NewContext().PushSubscriptions.ToListAsync())
            .Select(s => s.Id).Should().Equal([live.Id]);

        forgotten.Id.Should().NotBe(live.Id);
    }

    /// <summary>
    /// Unsubscribing is scoped to its owner: an endpoint is not a way to silence somebody else.
    ///
    /// <para>The endpoint is a bearer string. It leaks — into a log, into a support ticket, into
    /// whatever a browser extension can read — and the predicate carrying the user id is what stops
    /// a leaked one being a remote «turn their notifications off» button.</para>
    /// </summary>
    [Fact]
    public async Task Unsubscribing_somebody_elses_endpoint_removes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var owner = harness.SeedUser(tenant.Id);
        var stranger = harness.SeedUser(tenant.Id);

        var device = await harness.SeedDeviceAsync(owner);

        (await harness.Subscriptions().RemoveAsync(stranger.Id, device.Endpoint, default))
            .Should().BeFalse();

        (await harness.NewContext().PushSubscriptions.ToListAsync()).Should().ContainSingle();

        (await harness.Subscriptions().RemoveAsync(owner.Id, device.Endpoint, default))
            .Should().BeTrue();

        (await harness.NewContext().PushSubscriptions.ToListAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// A device that comes back after a bad week is not one failure from being forgotten.
    ///
    /// <para>Re-subscribing is the device saying it is alive, which is exactly what the counter is
    /// counting the absence of.</para>
    /// </summary>
    [Fact]
    public async Task Re_subscribing_clears_the_failures_that_had_built_up()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        var device = await harness.SeedDeviceAsync(user);

        await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);
        await harness.Subscriptions().RecordAsync(device.Id, PushDelivery.Failed("500"), default);

        await harness.Subscriptions().SaveAsync(
            tenant.Id, user.Id, device.Endpoint, device.P256dh, device.Auth, "fa", default);

        (await harness.NewContext().PushSubscriptions.SingleAsync())
            .ConsecutiveFailures.Should().Be(0);
    }
}
