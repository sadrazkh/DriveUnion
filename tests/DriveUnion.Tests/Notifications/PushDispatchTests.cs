using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Notifications;
using DriveUnion.Infrastructure.Push;
using DriveUnion.Tests.Services;
using DriveUnion.Web.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// One event, to the devices it is for, in each of their own languages — and what a payload is
/// allowed to carry.
///
/// <para>The assertions that matter most here are the negative ones. A notification that fails to
/// arrive is a missing feature somebody notices; a notification carrying a customer's file name is
/// that name written onto a phone's lock screen and kept in a notification centre for days, in a
/// product sold on the server holding no readable copy — and nothing anywhere reports it.</para>
/// </summary>
public class PushDispatchTests
{
    // ── who it reaches ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_persons_event_reaches_their_devices_and_nobody_elses()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var reza = harness.SeedUser(tenant.Id);
        var sara = harness.SeedUser(tenant.Id);

        var phone = await harness.SeedDeviceAsync(reza);
        var laptop = await harness.SeedDeviceAsync(reza);
        await harness.SeedDeviceAsync(sara);

        var service = new FakePushService();

        var reached = await Dispatcher(harness, service).DeliverAsync(
            new PushEvent(PushEventKind.RemoteFetchCompleted, PushAudience.Person(tenant.Id, reza.Id)),
            default);

        reached.Should().Be(2);
        service.Reached.Should().BeEquivalentTo([phone.Id, laptop.Id]);
    }

    /// <summary>
    /// A workspace with nobody subscribed costs nothing at all.
    ///
    /// <para>Which is the ordinary case: most people never turn this on. A dispatcher that built a
    /// payload, or opened a client, before finding that out would be spending it on every deletion
    /// in the product.</para>
    /// </summary>
    [Fact]
    public async Task An_event_for_a_workspace_with_no_devices_sends_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var service = new FakePushService();

        var reached = await Dispatcher(harness, service).DeliverAsync(
            new PushEvent(PushEventKind.DeletionCompleted, PushAudience.Workspace(tenant.Id), 7),
            default);

        reached.Should().Be(0);
        service.Reached.Should().BeEmpty();
    }

    /// <summary>
    /// Each device is composed in its own language.
    ///
    /// <para>Two people in one workspace can be reading the panel in two languages, and by the time
    /// a notification is on a lock screen there is no language switch to press. There is also no
    /// request behind a push, so the culture cannot come from the thread — it comes off the row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Each_device_is_written_in_the_language_it_subscribed_in()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        await harness.SeedDeviceAsync(user, culture: "fa");
        await harness.SeedDeviceAsync(user, culture: "en");

        var service = new FakePushService();

        await Dispatcher(harness, service).DeliverAsync(
            new PushEvent(PushEventKind.RemoteFetchFailed, PushAudience.Workspace(tenant.Id)),
            default);

        service.Payloads.Select(Title).Should().BeEquivalentTo(
            ["RemoteFetchFailed:fa", "RemoteFetchFailed:en"]);
    }

    // ── what a payload may carry ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Nothing of the customer's crosses the wire.</b>
    ///
    /// <para>Composed through the panel's own <c>PushMessages</c> rather than a fake, because the
    /// question is about the words that would really be sent. The file that was fetched has a name,
    /// the workspace has a name, the link has a slug — and none of the three is anywhere in the four
    /// fields a payload has. A tap on the notification goes to a path behind the reader's own
    /// session, which is where those names live and where they stay.</para>
    /// </summary>
    [Theory]
    [InlineData(PushEventKind.RemoteFetchCompleted)]
    [InlineData(PushEventKind.RemoteFetchFailed)]
    [InlineData(PushEventKind.DeletionCompleted)]
    [InlineData(PushEventKind.AbuseReportFiled)]
    public async Task A_payload_names_no_file_no_workspace_and_no_link(PushEventKind kind)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "Q3-Report-Final.pdf");
        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        var staff = harness.SeedUser(tenantId: null, isOperator: true);
        var customer = harness.SeedUser(tenant.Id);

        await harness.SeedDeviceAsync(staff, culture: "en");
        await harness.SeedDeviceAsync(customer, culture: "en");

        var service = new FakePushService();

        var audience = kind == PushEventKind.AbuseReportFiled
            ? PushAudience.OperatorStaff
            : PushAudience.Workspace(tenant.Id);

        await Dispatcher(harness, service, new PushMessages()).DeliverAsync(
            new PushEvent(kind, audience, 7),
            default);

        service.Payloads.Should().NotBeEmpty("this test is worthless if nothing was sent");

        foreach (var payload in service.Payloads)
        {
            payload.Should().NotContain("Q3-Report-Final", "a file name must not reach a lock screen");
            payload.Should().NotContain("acme", "nor a workspace name");
            payload.Should().NotContain("kx91mzq4", "nor a slug, which is the link itself");
            payload.Should().NotContain(tenant.Id.ToString(), "nor an identifier of any row");
            payload.Should().NotContain(file.Id.ToString());
        }
    }

    /// <summary>
    /// The payload is the four short fields the worker reads, and a path rather than an address.
    ///
    /// <para>An absolute URL would be this deployment's current host baked into a message that may
    /// be drawn days later, on a phone, after the panel has moved.</para>
    /// </summary>
    [Fact]
    public async Task A_payload_is_four_fields_and_the_url_is_a_path()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        await harness.SeedDeviceAsync(user, culture: "en");

        var service = new FakePushService();

        await Dispatcher(harness, service, new PushMessages()).DeliverAsync(
            new PushEvent(PushEventKind.DeletionCompleted, PushAudience.Workspace(tenant.Id), 7),
            default);

        using var payload = JsonDocument.Parse(service.Payloads.Single());
        var fields = payload.RootElement;

        fields.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["t", "b", "u", "g"]);
        fields.GetProperty("u").GetString().Should().StartWith("/").And.NotContain("://");
        fields.GetProperty("t").GetString().Should().NotBeNullOrWhiteSpace();

        // The number of files is the one thing that travels, and a number of files is not a file.
        fields.GetProperty("b").GetString().Should().Contain("7");
    }

    /// <summary>
    /// A payload fits in one record, in the longer of the two languages, with room to spare.
    ///
    /// <para>4096 bytes less the tag and the delimiter. Past it <c>Encrypt</c> refuses — which is
    /// far better than a record longer than its own header claims, but is still a notification that
    /// never arrives. Persian is the longer of the two and is what this measures.</para>
    /// </summary>
    [Theory]
    [InlineData(PushEventKind.RemoteFetchCompleted)]
    [InlineData(PushEventKind.RemoteFetchFailed)]
    [InlineData(PushEventKind.DeletionCompleted)]
    [InlineData(PushEventKind.AbuseReportFiled)]
    public void Every_message_fits_in_one_record(PushEventKind kind)
    {
        var text = new PushMessages().Compose(kind, 999_999, "fa");

        var payload = JsonSerializer.Serialize(new PushPayload(text.Title, text.Body, text.Url, text.Tag));

        System.Text.Encoding.UTF8.GetByteCount(payload)
            .Should().BeLessThan(WebPushEncryption.MaxPlaintextLength);
    }

    // ── pruning, from the side that learns about it ──────────────────────────────────────────────

    /// <summary>
    /// <b>A device the push service says is gone is removed as part of the send.</b>
    ///
    /// <para>The answer arrives here and nowhere else will ever get a better one. Deferring it to a
    /// sweep would mean the sweep has to guess, and in the meantime every notification for this
    /// workspace carries the cost of a device that does not exist.</para>
    /// </summary>
    [Fact]
    public async Task A_device_that_answers_gone_is_removed_while_the_rest_are_still_delivered()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);

        var dead = await harness.SeedDeviceAsync(user);
        var live = await harness.SeedDeviceAsync(user);

        var service = new FakePushService();
        service.Answer(PushDelivery.Gone("410"), PushDelivery.Accepted);

        var reached = await Dispatcher(harness, service).DeliverAsync(
            new PushEvent(PushEventKind.RemoteFetchCompleted, PushAudience.Workspace(tenant.Id)),
            default);

        reached.Should().Be(1);

        // The second device was still tried. A dead endpoint must not end the pass — the rest of the
        // workspace is waiting behind it.
        service.Reached.Should().HaveCount(2);

        (await harness.NewContext().PushSubscriptions.ToListAsync())
            .Select(s => s.Id).Should().Equal([live.Id]);

        dead.Id.Should().NotBe(live.Id);
    }

    /// <summary>
    /// Failures count towards the ceiling on the row rather than being retried inside one pass.
    ///
    /// <para>A notification is not worth a retry loop: the panel already holds the answer and the
    /// next event will try again. What the counter is for is telling a bad afternoon from an
    /// endpoint that has stopped working.</para>
    /// </summary>
    [Fact]
    public async Task A_failed_delivery_is_counted_on_the_row_and_not_retried()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var user = harness.SeedUser(tenant.Id);
        await harness.SeedDeviceAsync(user);

        var service = new FakePushService { Always = PushDelivery.Failed("503") };

        var reached = await Dispatcher(harness, service).DeliverAsync(
            new PushEvent(PushEventKind.RemoteFetchCompleted, PushAudience.Workspace(tenant.Id)),
            default);

        reached.Should().Be(0);
        service.Reached.Should().ContainSingle("one attempt per device per event");

        (await harness.NewContext().PushSubscriptions.SingleAsync())
            .ConsecutiveFailures.Should().Be(1);
    }

    private static PushDispatcher Dispatcher(
        ServiceTestHarness harness,
        IWebPushSender sender,
        IPushMessages? messages = null) =>
        new(
            harness.Subscriptions(),
            sender,
            messages ?? new FakePushMessages(),
            NullLogger<PushDispatcher>.Instance);

    private static string? Title(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        return document.RootElement.GetProperty("t").GetString();
    }
}
