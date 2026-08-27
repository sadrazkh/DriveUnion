using DriveUnion.Core.Notifications;

namespace DriveUnion.Core.Application;

/// <summary>
/// The things this product is willing to wake a phone for.
///
/// <para>The list is short on purpose and the omissions are the decision. An ordinary upload is not
/// here because the reader is watching it — the dock draws its progress on every screen in the panel
/// and the phone is in their hand. A download of somebody else's link is not here because it is the
/// visitor's action and not the owner's. A share link created, a file restored, a plan changed: all
/// of those happen in the request that asked for them, and a notification for something that has
/// already been confirmed on screen is a notification that teaches its reader to dismiss the next
/// one unread.</para>
///
/// <para>What is left is the three cases where the person who asked is expected to be somewhere
/// else: a fetch the server was told to run <i>because</i> the customer's machine could be asleep, a
/// deletion big enough to have been queued, and a complaint about a public link — that last one for
/// the operator only, and only because it is racing Google. See <c>AbuseReport</c> for what losing
/// that race costs.</para>
/// </summary>
public enum PushEventKind
{
    /// <summary>A link-upload landed. <c>RemoteFetch</c>.</summary>
    RemoteFetchCompleted,

    /// <summary>A link-upload was given up on. <c>RemoteFetch</c>.</summary>
    RemoteFetchFailed,

    /// <summary>A queued deletion has nothing left to move. <c>DeletionJob</c>.</summary>
    DeletionCompleted,

    /// <summary>Somebody complained about a public link. Operator staff only.</summary>
    AbuseReportFiled,
}

/// <summary>
/// Whose devices a notification is for.
///
/// <para>Three shapes and no more, because every widening of this is a customer's news arriving on
/// somebody else's phone. <see cref="Operators"/> is not a tenant with a special id — it is a
/// question asked of the user rows, so somebody who stops being staff stops being told.</para>
/// </summary>
/// <param name="Operators">Every signed-up device belonging to operator staff.</param>
/// <param name="TenantId">Every device in one workspace, when <paramref name="UserId"/> is null.</param>
/// <param name="UserId">One person's devices, in that workspace.</param>
public readonly record struct PushAudience(bool Operators, Guid? TenantId, Guid? UserId)
{
    /// <summary>Operator staff, wherever they are. Nothing about a workspace reaches them here.</summary>
    public static PushAudience OperatorStaff => new(true, null, null);

    /// <summary>Everybody in one workspace. For work nobody in particular asked for.</summary>
    public static PushAudience Workspace(Guid tenantId) => new(false, tenantId, null);

    /// <summary>
    /// One person, and the workspace is still carried.
    ///
    /// <para>Both, not just the user id: a user id on its own would be a lookup with no tenant in
    /// it, and this product has no global query filter precisely so that every scoped read says
    /// whose it is. See <c>DriveUnionDbContext.OnModelCreating</c>.</para>
    /// </summary>
    public static PushAudience Person(Guid tenantId, Guid userId) => new(false, tenantId, userId);
}

/// <summary>
/// Something worth telling somebody about, and nothing about what it happened to.
///
/// <para><b>There is no file name here and there is never going to be one.</b> A push payload is
/// encrypted to a device and decrypted by a service worker, and from that moment it is the phone's
/// to keep: an operating system draws it on a lock screen, files it in a notification centre and
/// may hold it there for days. This product is sold on the server keeping no readable copy of a
/// customer's files, and a phone quietly accumulating «Q3-Report-Final.pdf finished» is that claim
/// with an exception in it. So what travels is a kind and, at most, a count — enough for a sentence
/// worth reading, and useless to anybody who reads the phone over its owner's shoulder.</para>
/// </summary>
/// <param name="Count">
/// A number the sentence can carry — files in a deletion, and nothing else so far. Zero when the
/// sentence has no number in it.
/// </param>
public sealed record PushEvent(PushEventKind Kind, PushAudience Audience, int Count = 0);

/// <summary>
/// Where the code that finished something says so.
///
/// <para><b>It does not send anything, and that is the point.</b> <c>RemoteFetcher</c> and
/// <c>DeletionRunner</c> are workers with a budget, and an HTTP request to a push service that hangs
/// for thirty seconds is thirty seconds those workers are not moving files. The one that raises an
/// event and the one that spends a socket on it are deliberately different threads, and this is the
/// line between them.</para>
///
/// <para>Raising never throws and never blocks. A notification is a courtesy on top of work that has
/// already succeeded; taking a deletion down because a queue was full would be the tail wagging the
/// dog.</para>
/// </summary>
public interface IPushEvents
{
    void Raise(PushEvent notification);
}

/// <summary>
/// The half that spends the sockets: audience to devices, devices to encrypted bodies, bodies to the
/// push services, and dead endpoints to nothing.
/// </summary>
public interface IPushDispatcher
{
    /// <summary>
    /// Delivers one event to every device it is for.
    ///
    /// <para>Returns how many devices were reached, which is a figure a test can hold and a log line
    /// can carry. A subscription that turns out to be gone is removed as part of this rather than
    /// left for a sweep, because the answer arrived here and nowhere else will ever get a better one.
    /// </para>
    /// </summary>
    Task<int> DeliverAsync(PushEvent notification, CancellationToken cancellationToken);
}

/// <summary>What a device did with the message we tried to give it.</summary>
public enum PushDeliveryOutcome
{
    /// <summary>The push service took it.</summary>
    Accepted,

    /// <summary>
    /// The push service says there is no such endpoint — a 404 or a 410.
    ///
    /// <para>The one answer in this protocol that is not ambiguous: the browser profile is gone, the
    /// subscription was revoked, or the endpoint expired. There is nothing to retry and nothing to
    /// count. The row goes.</para>
    /// </summary>
    Gone,

    /// <summary>
    /// Anything else — a 500, a timeout, a refused connection, a 403 from a wrong VAPID key.
    ///
    /// <para>Counted rather than fatal, because the first three of those are usually a bad minute.
    /// A 403 is not, and it is deliberately not special-cased: a deployment whose keys are wrong is
    /// a deployment where <i>every</i> endpoint fails, so treating it as fatal per row would empty
    /// the whole table over a configuration mistake somebody can fix in a minute.</para>
    /// </summary>
    Failed,
}

/// <summary>One attempt at one device.</summary>
/// <param name="Reason">Kept on the row for whoever looks, and shown to nobody.</param>
public readonly record struct PushDelivery(PushDeliveryOutcome Outcome, string? Reason)
{
    public static PushDelivery Accepted => new(PushDeliveryOutcome.Accepted, null);

    public static PushDelivery Gone(string reason) => new(PushDeliveryOutcome.Gone, reason);

    public static PushDelivery Failed(string reason) => new(PushDeliveryOutcome.Failed, reason);
}

/// <summary>Posting one encrypted body to one push service. The only thing here that needs a socket.</summary>
public interface IWebPushSender
{
    Task<PushDelivery> SendAsync(
        PushSubscription subscription,
        string payload,
        CancellationToken cancellationToken);
}

/// <summary>
/// The words on the lock screen.
///
/// <para>Implemented in the web project rather than here, because every user-visible string in this
/// product comes from <c>UiText</c> and is chosen by <c>PanelCulture</c> — and the caller is a
/// background worker with no request to read a culture from. So the culture is an argument, taken
/// from the device's own row.</para>
/// </summary>
public interface IPushMessages
{
    PushNotificationText Compose(PushEventKind kind, int count, string culture);
}

/// <summary>
/// What a service worker draws, and the whole of what crosses the wire.
/// </summary>
/// <param name="Url">
/// Where a tap goes. A path and not an address, so a deployment behind a different host does not
/// send its customers somewhere that no longer exists.
/// </param>
/// <param name="Tag">
/// The notification's identity on the device. Two events of the same kind collapse into one entry
/// rather than stacking up, which is what stops a phone with a hundred deletions on it.
/// </param>
public sealed record PushNotificationText(string Title, string Body, string Url, string Tag);

/// <summary>
/// The devices, and the bookkeeping that removes them.
///
/// <para>Every read carries the audience it is for. There is no global query filter in this model
/// and there must not be one — see <c>DriveUnionDbContext.OnModelCreating</c> — so «whose devices»
/// is an argument at every call site, in this slice as in every other.</para>
/// </summary>
public interface IPushSubscriptions
{
    /// <summary>
    /// Records a device, or refreshes the one already at that endpoint.
    ///
    /// <para>Upsert rather than insert: a browser hands back the same endpoint every time it is
    /// asked, so a second subscribe from the same device is the same mailbox and not a new one. It
    /// also moves the row to whoever is signed in now, which is what makes a shared computer behave
    /// — the device stops being the previous person's and becomes this one's, rather than notifying
    /// both.</para>
    /// </summary>
    Task<PushSubscriptionSaved> SaveAsync(
        Guid? tenantId,
        Guid userId,
        string endpoint,
        string p256dh,
        string auth,
        string culture,
        CancellationToken cancellationToken);

    /// <summary>Forgets one device. Scoped to its owner, so an endpoint is not a way to unsubscribe somebody else.</summary>
    Task<bool> RemoveAsync(Guid userId, string endpoint, CancellationToken cancellationToken);

    /// <summary>Every device an event is for.</summary>
    Task<IReadOnlyList<PushSubscription>> ForAsync(PushAudience audience, CancellationToken cancellationToken);

    /// <summary>How many devices this person has registered, for the screen that says so.</summary>
    Task<int> CountForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies what a push service said about one device.
    ///
    /// <para>The pruning lives here rather than at the call site so that «gone means gone» is one
    /// decision in one place: a 404 deletes, a run of failures deletes, and a success clears the
    /// count. A sender that decided for itself would be a second copy of the rule, and the second
    /// copy is the one that ends up keeping dead rows.</para>
    /// </summary>
    Task RecordAsync(Guid subscriptionId, PushDelivery delivery, CancellationToken cancellationToken);

    /// <summary>
    /// Removes devices nothing has heard from in <see cref="PushSubscription.StaleAfter"/>.
    ///
    /// <para>The other half of pruning, and the one the failure counter cannot do: an endpoint that
    /// is never sent to is never found to be dead. Returns how many went.</para>
    /// </summary>
    Task<int> SweepStaleAsync(CancellationToken cancellationToken);
}

/// <summary>Why a subscribe was refused, or that it was not.</summary>
public enum PushSubscriptionRefusal
{
    None,

    /// <summary>An endpoint, key or secret that is not the shape the browser produces.</summary>
    Malformed,

    /// <summary>This person already has <see cref="PushSubscription.MostPerUser"/> devices.</summary>
    TooMany,
}

public readonly record struct PushSubscriptionSaved(Guid? Id, PushSubscriptionRefusal Refusal)
{
    public bool Ok => Refusal is PushSubscriptionRefusal.None;
}
