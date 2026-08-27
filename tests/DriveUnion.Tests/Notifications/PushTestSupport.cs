using System.Buffers.Text;
using System.Security.Cryptography;
using DriveUnion.Core.Application;
using DriveUnion.Core.Notifications;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Push;
using DriveUnion.Tests.Services;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// A device, a person and a push service, for the tests that are about what happens between them.
/// </summary>
internal static class PushTestSupport
{
    public static PushSubscriptionStore Subscriptions(
        this ServiceTestHarness harness,
        DriveUnionDbContext? context = null) =>
        new(context ?? harness.Db, harness.Clock);

    /// <summary>
    /// A real device's keys, generated rather than made up.
    ///
    /// <para>A P-256 point and sixteen random bytes, which is exactly what a browser hands over — so
    /// a test that seeds one is seeding something the encryption can actually be run against, and
    /// «the store accepted a key nothing could encrypt to» is not a state these tests can reach.
    /// </para>
    /// </summary>
    public static (string P256dh, string Auth) DeviceKeys()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        return (
            Base64Url.EncodeToString(WebPushEncryption.UncompressedPoint(key)),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    /// <summary>Somebody who can sign in, because the operator audience is a question about user rows.</summary>
    public static AppUser SeedUser(
        this ServiceTestHarness harness,
        Guid? tenantId = null,
        bool isOperator = false)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsOperator = isOperator,
            UserName = $"{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"{Guid.NewGuid():N}@EXAMPLE.TEST",
            Email = $"{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = ServiceTestHarness.Now,
        };

        harness.Db.Users.Add(user);
        harness.Db.SaveChanges();

        return user;
    }

    /// <summary>A registered device for that person, straight into the table.</summary>
    public static async Task<PushSubscription> SeedDeviceAsync(
        this ServiceTestHarness harness,
        AppUser user,
        string culture = "fa")
    {
        var (p256dh, auth) = DeviceKeys();

        var saved = await harness.Subscriptions().SaveAsync(
            user.TenantId,
            user.Id,
            $"https://push.example.test/{Guid.NewGuid():N}",
            p256dh,
            auth,
            culture,
            default);

        return harness.Db.PushSubscriptions.Single(s => s.Id == saved.Id);
    }
}

/// <summary>
/// A push service that answers whatever the test tells it to, and remembers what it was asked.
///
/// <para>What the tests using it are about is what happens to the <i>row</i> afterwards — accepted
/// clears the counter, gone deletes it, failed counts — so the interesting part of this fake is the
/// answer it gives and not the request it received.</para>
/// </summary>
internal sealed class FakePushService : IWebPushSender
{
    private readonly Queue<PushDelivery> _answers = new();

    /// <summary>Used once each, oldest first, and <see cref="Always"/> after they run out.</summary>
    public PushDelivery Always { get; set; } = PushDelivery.Accepted;

    /// <summary>Every payload, in order, so a test can read what would have reached a lock screen.</summary>
    public List<string> Payloads { get; } = [];

    /// <summary>Every device it was asked to reach.</summary>
    public List<Guid> Reached { get; } = [];

    public void Answer(params PushDelivery[] answers)
    {
        foreach (var answer in answers) _answers.Enqueue(answer);
    }

    public Task<PushDelivery> SendAsync(
        PushSubscription subscription,
        string payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        Reached.Add(subscription.Id);
        Payloads.Add(payload);

        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Always);
    }
}

/// <summary>
/// Words with the culture written into them, so a test can see which language a device was composed
/// in without depending on the panel's own Persian.
/// </summary>
internal sealed class FakePushMessages : IPushMessages
{
    public PushNotificationText Compose(PushEventKind kind, int count, string culture) =>
        new($"{kind}:{culture}", $"count={count}", "/files", kind.ToString().ToLowerInvariant());
}
