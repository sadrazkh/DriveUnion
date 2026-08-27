namespace DriveUnion.Core.Notifications;

/// <summary>
/// One browser, on one device, that has agreed to be woken up.
///
/// <para><b>It is a device and not a person.</b> A customer who installs the panel on a phone and
/// keeps it open on a desktop has two of these rows, and a person who has neither has none. That is
/// the whole reason <see cref="ConsecutiveFailures"/> exists: the row outlives the browser profile
/// it was minted in, and nothing ever tells us the device was wiped. An endpoint that has stopped
/// existing is a queue entry that is retried for ever unless something counts.</para>
///
/// <para><b>What is stored is a mailbox and two keys, and nothing that can be read.</b>
/// <see cref="Endpoint"/> is a URL at the browser vendor's push service; <see cref="P256dh"/> and
/// <see cref="Auth"/> are the device's own public key and its authentication secret, which together
/// are what a payload is encrypted <i>to</i>. Holding them lets this server write a message only
/// that device can open — it does not let this server, or the push service in the middle, open one.
/// See <c>WebPushEncryption</c>.</para>
///
/// <para><b>No foreign key to <c>Tenant</c>, and that is not an omission.</b> The same reasoning as
/// <c>UploadSession</c>, <c>RemoteFetch</c>, <c>AbuseReport</c> and <c>DeletionJob</c>:
/// <c>TenantStorageMeter</c> reserves quota with <c>ExecuteUpdate</c> and then detaches the tenant it
/// went round, and detaching a principal cascade-detaches its tracked dependents — so a row tied to
/// it by a real key stops being written halfway through its own work. It is written out in full in
/// the model configuration.</para>
/// </summary>
public sealed class PushSubscription
{
    public Guid Id { get; set; }

    /// <summary>
    /// Whose workspace this device belongs to, or null for operator staff, who have none.
    ///
    /// <para>Nullable rather than <c>Guid.Empty</c>, exactly as <c>AppUser.TenantId</c> is: an empty
    /// id in a scoped query matches nothing and looks like a workspace with no devices, which is a
    /// customer who is never notified and nothing anywhere saying why.</para>
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Who was signed in when the device was subscribed.
    ///
    /// <para>Always set — a subscription is offered only to somebody signed in — and it is what
    /// makes «tell the person who asked for this fetch» possible rather than «tell the workspace».
    /// It is also how an operator's devices are found: the operator claim lives on the user row, not
    /// here, so that a person who stops being staff stops being notified about abuse reports on
    /// their next report rather than on a migration.</para>
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The push service's address for this device. Unique across the table.
    ///
    /// <para>Unique because it is the device's identity as far as anything here is concerned: a
    /// browser that re-subscribes with the same keys hands back the same endpoint, and two rows for
    /// one endpoint would be one notification arriving twice on one phone.</para>
    /// </summary>
    public required string Endpoint { get; set; }

    /// <summary>
    /// The device's public key, base64url, as the browser's <c>PushSubscription.getKey('p256dh')</c>
    /// gives it: an uncompressed P-256 point, 65 bytes.
    /// </summary>
    public required string P256dh { get; set; }

    /// <summary>The device's authentication secret, base64url. Sixteen bytes.</summary>
    public required string Auth { get; set; }

    /// <summary>
    /// The panel's language on the device that subscribed.
    ///
    /// <para>Stored because a notification is composed by a background worker, which has no request,
    /// no cookie and therefore no culture. Without it every notification would be written in the
    /// product's default language and an English-reading operator would be woken by a Persian
    /// sentence — which is the one place in this product a reader cannot press the language switch,
    /// because the words are already on the lock screen.</para>
    /// </summary>
    public required string Culture { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The last time this device said it was still there — re-subscribed, or accepted a push.
    ///
    /// <para>Not «last notified»: a device with nothing to be told about is not a dead one. What
    /// this is for is telling a subscription that is merely quiet from one whose browser profile was
    /// deleted a year ago, for the sweep that removes the second kind.</para>
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// How many sends in a row have failed, reset to zero by any success.
    ///
    /// <para>A 404 or a 410 from the push service is «this endpoint is gone» and deletes the row
    /// outright — that answer is unambiguous and there is nothing to count. This counter is for
    /// everything else: a 500 from the push service, a timeout, a network that was not there. Any of
    /// those may be a bad minute, so one is not fatal; all of them look identical to an endpoint
    /// that is quietly broken for ever, so <see cref="MaxConsecutiveFailures"/> of them is.</para>
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>What went wrong most recently, for whoever looks. Never shown to a reader.</summary>
    public string? LastFailureReason { get; set; }

    /// <summary>
    /// How many failures in a row end a subscription.
    ///
    /// <para>Five. A push service having a bad afternoon must not cost a customer their phone
    /// notifications, and an endpoint that has failed five separate times has not had an afternoon —
    /// it has stopped working. The failure this bounds is not wasted requests, it is a queue that
    /// never drains: without a ceiling, every notification for the life of the deployment carries the
    /// cost of every device that has ever been thrown away.</para>
    /// </summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>
    /// How long a device may go without being seen before it is swept.
    ///
    /// <para>Ninety days. Push services expire endpoints on their own schedules and do not tell
    /// anybody; a device that has neither opened the panel nor accepted a push in three months is a
    /// row whose only remaining function is to be tried and fail.</para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(90);

    /// <summary>
    /// Long enough for every push service in use and short enough to index.
    ///
    /// <para>Apple's endpoints run to about 190 characters, Google's to about 180 and Mozilla's to
    /// about 110. A kilobyte is five times the longest of them and stays well inside Postgres's
    /// btree limit, which matters because <see cref="Endpoint"/> carries a unique index — a longer
    /// column would be an index that starts refusing rows at run time rather than at deploy time.
    /// </para>
    /// </summary>
    public const int MaxEndpointLength = 1024;

    /// <summary>65 bytes, base64url, is 87 characters. The rest is room for padding somebody sent.</summary>
    public const int MaxP256dhLength = 128;

    /// <summary>16 bytes, base64url, is 22 characters.</summary>
    public const int MaxAuthLength = 32;

    public const int MaxFailureReasonLength = 256;

    /// <summary>
    /// How many devices one person may register.
    ///
    /// <para>Twenty. Nobody has twenty devices; a browser that re-subscribes under a new endpoint
    /// every time its profile is cleared does, and without a cap that is a table growing for ever
    /// with rows that will each be tried five times before they are removed.</para>
    /// </summary>
    public const int MostPerUser = 20;
}
