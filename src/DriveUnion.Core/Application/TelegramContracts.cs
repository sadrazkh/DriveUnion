using DriveUnion.Core.Telegram;

namespace DriveUnion.Core.Application;

/// <summary>
/// The three roles inside a tenant, with the gaps and the explicit values M5's design gives them, so
/// a fourth role can be inserted later without renumbering stored rows.
///
/// It is declared here because the Telegram resolver is the first thing in the product that has to
/// answer "what may this person do" without an <c>HttpContext</c> to ask, and M5 has not landed. When
/// it does, this enum moves to <c>Core.Tenancy</c> beside <c>Tenant</c> and nothing else changes:
/// only <c>TelegramIdentityReader</c> produces a value of it today.
/// </summary>
public enum TenantRole : byte
{
    Viewer = 10,
    Uploader = 20,
    Owner = 30,
}

/// <summary>Who a Telegram sender turned out to be. Every field is read from the database, never cached.</summary>
public sealed record TelegramIdentity(Guid AppUserId, Guid TenantId, TenantRole Role);

/// <summary>
/// The one place a Telegram sender id becomes a tenant, and the sibling of <c>IPublicLinkReader</c>.
///
/// <para><b>There is no tenant parameter here and there must never be one, nor an overload that
/// takes one.</b> A Telegram update arrives with no cookie, no principal and no tenant; there is
/// nobody to take a tenant from. A reader that accepted one would be handed <c>Guid.Empty</c> by the
/// only caller it has, and would then resolve every bound customer in the product to nothing while
/// their rows sat plainly in the table — the same failure <c>/d/{slug}</c> would have had under a
/// global query filter, in a place where the alternative mistake is worse: this call decides which
/// tenant's files a chat may read.</para>
///
/// <para>Null is the answer for everyone unbound, and it is the <em>only</em> other answer. A caller
/// that gets null must reply with one string and the same one every time — telling "never linked"
/// apart from "unlinked yesterday" turns the bot into an oracle for which Telegram accounts are
/// customers of this service, and anyone in the world can make the bot answer.</para>
/// </summary>
public interface ITelegramIdentityReader
{
    Task<TelegramIdentity?> ResolveAsync(long telegramUserId, CancellationToken cancellationToken);
}

/// <summary>One entry in the menu Telegram renders for the bot.</summary>
public sealed record TelegramBotCommand(string Command, string Description);

/// <summary>
/// Everything this product asks of Telegram, and the single place the two rate limiters sit in front
/// of.
///
/// <para><b>It is to Telegram what <c>IDriveClient</c> is to Google</b>, and for the same reason: this
/// machine has no bot token, no <c>api_id</c> and no Bot API server, so every rule worth testing —
/// which tenant a chat may read, what a 429 does to a queued item, whether an oversized file ever
/// reaches an upload, whether the local copy is deleted when the send throws — has to be provable
/// without a network. Every test in the suite runs against a hand-written in-memory Telegram.</para>
///
/// <para>Nothing here throws for an ordinary refusal. A 429, a 403 from a customer who blocked the
/// bot and a 400 from a stale message id are all things a caller must <em>do</em> something about, and
/// an exception is the shape that makes "retry for ever" the accidental default.</para>
/// </summary>
public interface ITelegramBotGateway
{
    Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutgoingMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Edits a message in place. A slow action edits the message it started from rather than
    /// appending new ones, so a chat does not fill up with progress.
    /// </summary>
    Task<TelegramCall<TelegramSentMessage>> EditMessageAsync(
        TelegramMessageEdit edit,
        CancellationToken cancellationToken);

    Task<TelegramCall<TelegramAck>> DeleteMessageAsync(
        long chatId,
        long messageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The «uploading…» indicator, which lasts about five seconds and therefore has to be repeated
    /// for the life of a long transfer. Sent once at the start of a four-minute upload, the chat
    /// looks idle for the other three minutes and fifty-five seconds — the exact appearance of a
    /// broken bot. It is not a message and does not spend the per-chat message budget.
    /// </summary>
    Task<TelegramCall<TelegramAck>> SendChatActionAsync(
        long chatId,
        string action,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called on every callback without exception. A button that spins for ever is the most common
    /// way a bot looks broken.
    /// </summary>
    Task<TelegramCall<TelegramAck>> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken);

    /// <summary>
    /// The one call that moves bytes. <paramref name="content"/> is null when
    /// <c>TelegramDocumentSend.CachedFileId</c> is set — a re-send from a cached handle reads nothing,
    /// costs no egress and touches no disk.
    /// </summary>
    Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramDocumentSend send,
        Stream? content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Where an inbound file's bytes are. The answer is a local path against our own server and a URL
    /// against the cloud API, and both branches are live.
    /// </summary>
    Task<TelegramCall<TelegramFileHandle>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the URL branch of <see cref="GetFileAsync"/>. The URL carries the bot token in its path,
    /// so it goes from that call straight into this one and is never logged, persisted or rendered.
    /// </summary>
    Task<TelegramCall<Stream>> OpenRemoteFileAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>The only proof a token works, and where the bot's real @username and id come from.</summary>
    Task<TelegramCall<TelegramBotProfile>> GetMeAsync(CancellationToken cancellationToken);

    Task<TelegramCall<TelegramAck>> SetWebhookAsync(
        string url,
        string secretToken,
        int maxConnections,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the registration. <c>drop_pending_updates</c> is never set: updates arriving during a
    /// change window are held by Telegram for 24 hours, and dropping them is the one way to turn a
    /// short outage into lost customer files.
    /// </summary>
    Task<TelegramCall<TelegramAck>> DeleteWebhookAsync(CancellationToken cancellationToken);

    Task<TelegramCall<TelegramWebhookInfo>> GetWebhookInfoAsync(CancellationToken cancellationToken);

    Task<TelegramCall<IReadOnlyList<TelegramUpdate>>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken);

    Task<TelegramCall<TelegramAck>> SetMyCommandsAsync(
        IReadOnlyList<TelegramBotCommand> commands,
        CancellationToken cancellationToken);

    /// <summary>
    /// The convenience the linking flow has always used: send one line and say whether it arrived.
    ///
    /// It is a default implementation rather than a method every gateway repeats, because there is
    /// exactly one correct way to write it and a second copy is a second place to get the failure
    /// direction backwards.
    /// </summary>
    async Task<bool> TrySendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var sent = await SendMessageAsync(
            new TelegramOutgoingMessage(chatId, text),
            cancellationToken).ConfigureAwait(false);

        return sent.Ok;
    }
}

/// <summary>
/// The dedup ledger. <see cref="TryClaimAsync"/> returns false for an update that has been handled
/// before, and false is the good answer: the correct response to a redelivery is 200 and stop.
/// </summary>
public interface ITelegramUpdateLedger
{
    Task<bool> TryClaimAsync(long updateId, CancellationToken cancellationToken);

    /// <summary>Drops rows past their usefulness. Returns how many went, so a sweeper that deletes
    /// nothing is distinguishable from one that had nothing to do.</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns the body of a webhook POST into an update, and returns null for anything that is not one.
///
/// It is an interface so the endpoint that answers an anonymous POST holds no knowledge of the wire
/// format, and so a malformed body is a null rather than an exception on a route that anybody on the
/// box can reach.
/// </summary>
public interface ITelegramUpdateParser
{
    TelegramUpdate? Parse(string json);
}

/// <summary>What an update turned into, so a transport can report it without knowing what happened.</summary>
public enum TelegramUpdateOutcome
{
    /// <summary>Handled, or deliberately ignored. Either way, answer 200 and advance the offset.</summary>
    Handled = 0,

    /// <summary>Already seen. The action was performed exactly once, by the first delivery.</summary>
    Duplicate = 1,
}

/// <summary>
/// One update in, one outcome out — and everything between is short. Byte-moving work is queued and
/// the caller returns immediately: a handler that uploads two gigabytes before replying is a handler
/// Telegram will redeliver on top of, and each redelivery would start its own multi-gigabyte transfer.
/// </summary>
public interface ITelegramUpdateHandler
{
    Task<TelegramUpdateOutcome> HandleAsync(TelegramUpdate update, CancellationToken cancellationToken);
}

public enum TelegramEnqueueStatus
{
    Queued = 0,

    /// <summary>Over the item bound or the byte bound. A bounded queue with an honest message beats
    /// an unbounded one that looks like it is working.</summary>
    QueueFull = 1,
}

public sealed record TelegramEnqueueResult(TelegramEnqueueStatus Status, Guid? ItemId);

/// <summary>The queue's writing end, which is all a chat handler ever needs.</summary>
public interface ITelegramOutboxWriter
{
    /// <param name="senderUserId">
    /// Who sent it, for an item that came from a person, and null for anything the bot raises of its
    /// own accord. It is what puts an inbound file in the sender's Drive folder rather than the
    /// workspace's — see TelegramOutbox.SenderUserId.
    /// </param>
    Task<TelegramEnqueueResult> EnqueueAsync(
        Guid tenantId,
        Guid? senderUserId,
        long chatId,
        TelegramOutboxKind kind,
        Guid? storedFileId,
        string? payload,
        long sizeBytes,
        DateTimeOffset? notBefore,
        CancellationToken cancellationToken);
}

/// <summary>
/// Everything the drainer needs to read one file's bytes: which account physically holds it and what
/// it is called there.
///
/// <para><b>This record never leaves the drainer</b>, and there is deliberately no path from a chat to
/// any of it. <c>IFileCatalog</c> is what the bot's surface reads, and it names no account on purpose
/// — the customer must never learn that a pool exists. This is the same shape and the same discipline
/// as the public download path's server-side ticket: the two identifying fields exist because
/// <c>IDriveClient</c> needs them, and they must never reach a message, a log line or a card.</para>
/// </summary>
/// <param name="IsEncrypted">
/// Whether what is stored is ciphertext, and therefore whether this delivery can happen at all.
///
/// <para>The drainer is the third of the three paths that cannot decrypt — the API's content route
/// and the S3 gateway are the others — and it is the one where sending it anyway would be least
/// visible: a document arrives in a chat, with the right name and the right size, and is opened by
/// somebody days later. It is refused before the file is read, and the refusal says why.</para>
/// </param>
public sealed record TelegramDeliveryTicket(
    Guid StoredFileId,
    Guid GoogleAccountId,
    string DriveFileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    bool IsEncrypted = false);

/// <summary>
/// Resolves a queued delivery back to the bytes it names, scoped to the tenant on the outbox row.
///
/// Null for a file that is not this tenant's, is soft-deleted, or never existed — the same answer for
/// all three, because the drainer's caller is ultimately a button press from a client we do not
/// control.
/// </summary>
public interface ITelegramDeliverySource
{
    Task<TelegramDeliveryTicket?> ResolveAsync(
        Guid tenantId,
        Guid storedFileId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Free space on the volume the Bot API server writes into.
///
/// It is an interface because the arithmetic it feeds — refuse a transfer that cannot fit, before a
/// single byte is read out of storage — is the most important thing in this slice to be able to test,
/// and there is no way to make a real volume nearly full inside a unit test.
/// </summary>
public interface ITelegramDiskSpace
{
    /// <summary>Null when the path names nothing this process can measure.</summary>
    long? FreeBytesOn(string path);
}

/// <summary>
/// The operator's bot as a screen may see it. There is deliberately no way to get the token out of
/// this record: a screen that could print it is a screen that eventually will, into an HTML source
/// view, a browser cache, or a bug-report screenshot. <see cref="HasToken"/> is the whole of what a
/// browser is ever told.
/// </summary>
public sealed record StoredTelegramBot(
    bool HasToken,
    string? BotUsername,
    long? BotUserId,
    DateTimeOffset? UpdatedAt,
    bool HasWebhook = false,
    DateTimeOffset? WebhookRegisteredAt = null);

/// <summary>
/// The registration, as the code that answers a webhook POST needs it: an unguessable path segment
/// and the secret Telegram is told to send back in a header.
///
/// Both are read here and nowhere else. Neither ever reaches a view, a log line or a response.
/// </summary>
public sealed record TelegramWebhookRegistration(string PathSegment, string Secret);

public interface ITelegramBotSettingsStore
{
    /// <summary>What the panel may see.</summary>
    Task<StoredTelegramBot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The registered path segment and secret, or null when no webhook has been registered. For the
    /// endpoint that has to check an inbound POST, and for nothing else.
    /// </summary>
    Task<TelegramWebhookRegistration?> ReadWebhookAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records a fresh registration. Both values are generated by the caller and stored encrypted;
    /// the previous pair is replaced, which is what makes re-registering a rotation rather than an
    /// addition.
    /// </summary>
    Task SaveWebhookAsync(
        string pathSegment,
        string secret,
        CancellationToken cancellationToken);

    /// <summary>Forgets the registration. False when there was none.</summary>
    Task<bool> ClearWebhookAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What <c>getMe</c> said, which is the authoritative answer to both questions the token was only
    /// guessed at for: the bot's numeric id — the cache key every stored <c>file_id</c> hangs off —
    /// and the @username every customer's deep link is built from.
    /// </summary>
    Task SaveVerifiedProfileAsync(
        long botUserId,
        string botUsername,
        CancellationToken cancellationToken);

    /// <summary>
    /// The token in the clear, for the code that actually talks to Telegram and nothing else. Null
    /// when none is stored, or when the stored one no longer decrypts under the current key.
    /// </summary>
    Task<string?> ReadBotTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A null or empty <paramref name="botToken"/> keeps the token already stored, which is what
    /// makes "correct the @username" possible without fetching the token out of @BotFather again.
    /// </summary>
    Task<StoredTelegramBot> SaveAsync(
        string? botToken,
        string? botUsername,
        Guid? updatedByUserId,
        CancellationToken cancellationToken);

    /// <summary>Forgets the stored bot. False when there was nothing to forget.</summary>
    Task<bool> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The whole of what the operator's screen learns about who is using the bot: two numbers.
///
/// <b>The absence of a listing here is the point.</b> There is deliberately no method that returns
/// which customers have linked, no chat id, no @username and no filename — a cross-tenant directory
/// of customers' messenger identities is a privacy surface with no product use behind it, and the
/// cheapest way to keep it from being built by accident is for the read model to be incapable of
/// expressing it.
/// </summary>
public sealed record TelegramOperatorHealth(
    int LinkedAccounts,
    int PendingRequests,
    int OutboxDepth = 0,
    int UpdatesLastDay = 0,
    int SendsFailedLastDay = 0);

/// <summary>
/// The Bot API server, which nothing else in the product can see.
///
/// <para>Two of these are alarms rather than statistics. A rising <c>pending_update_count</c> is what
/// a broken webhook looks like from the outside while everything on this box appears perfectly
/// healthy. A working directory whose size stays above zero across several minutes is what a stopped
/// delete-on-success looks like while everything about the bot appears perfectly healthy — and it is
/// the one that fills the volume.</para>
///
/// <para>Here the good state is <see cref="WorkDirectoryBytes"/> at or near zero, so a delete count is
/// deliberately not the signal: deletion on success is the normal path and the sweeper is the crash
/// path, so a sweeper finding nothing is health rather than a fault. The test suite asserts the
/// opposite — seed old files, sweep, insist on a non-zero count — because that is what proves the code
/// can delete at all.</para>
/// </summary>
public sealed record TelegramServerHealth(
    string ApiBaseUrl,
    bool LocalBotServer,
    string? WorkDirectory,
    long WorkDirectoryBytes,
    int WorkDirectoryFiles,
    TimeSpan? OldestFileAge,
    long? FreeBytes);

public interface ITelegramOperatorView
{
    Task<TelegramOperatorHealth> ReadAsync(CancellationToken cancellationToken);

    /// <summary>What is on this box, read from the filesystem rather than from a table.</summary>
    TelegramServerHealth ReadServerHealth();
}

/// <summary>How the linking card should be drawn for one customer.</summary>
public sealed record TelegramLinkState(
    bool BotConfigured,
    string? BotUsername,
    TelegramLinkedAccount? Linked,
    TelegramPendingLink? Pending);

/// <summary>
/// The bound account, as the customer's settings card renders it.
///
/// <b>The numeric Telegram id is deliberately absent.</b> It is an identifier the customer has no use
/// for and support does not need on a screen, and a record that carried it would eventually print it.
/// </summary>
public sealed record TelegramLinkedAccount(
    string? Username,
    string? DisplayName,
    DateTimeOffset LinkedAt,
    TelegramDeliveryStatus DeliveryStatus);

/// <summary>
/// A request that has been started and not finished.
///
/// There is no deep link on this record, and there cannot be one: only the token's hash was stored,
/// so the link is unrecoverable the moment the response that carried it has been rendered. The card
/// therefore offers a fresh request rather than the old link, which is the same contract an API key
/// has.
/// </summary>
public sealed record TelegramPendingLink(
    DateTimeOffset ExpiresAt,
    bool CodeIssued,
    int AttemptsLeft);

public enum TelegramLinkStartStatus
{
    Issued = 0,

    /// <summary>The operator has configured no bot, so there is no @username to build a link from.</summary>
    BotNotConfigured = 1,

    AlreadyLinked = 2,

    /// <summary>Operator staff have no tenant, and the bot has nothing to show someone without one.</summary>
    NoTenant = 3,
}

/// <summary>
/// The deep link, handed over exactly once. <see cref="DeepLink"/> carries the raw token, so it is
/// written to the response that asked for it and to nothing else — no log line, no TempData, no
/// redirect, no second render.
/// </summary>
public sealed record TelegramLinkStart(
    TelegramLinkStartStatus Status,
    string? DeepLink,
    DateTimeOffset? ExpiresAt);

/// <summary>What the bot was shown, and who showed it.</summary>
public sealed record TelegramStartRequest(
    string? Token,
    long TelegramUserId,
    long ChatId,
    string? Username,
    string? DisplayName,
    string? LanguageCode);

public enum TelegramStartStatus
{
    /// <summary>A six-digit code was issued. <b>Nothing is bound yet.</b></summary>
    CodeIssued = 0,

    /// <summary>No token, an unknown one, an expired one, or one already spent.</summary>
    TokenNotUsable = 1,

    /// <summary>This Telegram account already belongs to some other panel user.</summary>
    AlreadyBoundElsewhere = 2,

    /// <summary>A <c>/start</c> with no token at all, from someone bound to nobody.</summary>
    Stranger = 3,

    /// <summary>A <c>/start</c> with no token, from someone already bound.</summary>
    AlreadyLinked = 4,
}

/// <summary>
/// What the bot's leg produced. <see cref="ReplyText"/> is always set, because a chat that gets no
/// answer is the failure mode this product keeps refusing to ship.
///
/// <see cref="ConfirmationCode"/> is the six digits, for the one caller that sends them into the
/// chat. It never reaches a log and never reaches the panel.
/// </summary>
public sealed record TelegramStartOutcome(
    TelegramStartStatus Status,
    string ReplyText,
    string? ConfirmationCode);

public enum TelegramConfirmStatus
{
    Linked = 0,

    /// <summary>Nothing was started, or what was started has expired or been spent.</summary>
    NoPendingRequest = 1,

    /// <summary>Started, but the bot has not been shown the deep link yet, so no code exists.</summary>
    NotPresented = 2,

    WrongCode = 3,

    /// <summary>The attempt budget is gone, or another request won the same token.</summary>
    TokenDead = 4,

    AlreadyLinked = 5,

    /// <summary>That Telegram account was bound to somebody else between the two legs.</summary>
    TelegramAccountTaken = 6,
}

public sealed record TelegramConfirmOutcome(TelegramConfirmStatus Status, int AttemptsLeft);

/// <summary>
/// Why a binding is being removed. It picks the farewell, and it is the reason the same command
/// serves the customer's own «قطع اتصال» and the deletion of their panel account.
/// </summary>
public enum TelegramUnlinkReason
{
    /// <summary>The customer pressed the button.</summary>
    Customer = 0,

    /// <summary>Their panel account is going away.</summary>
    PanelUserRemoved = 1,
}

/// <summary>
/// What was removed and what has to be said about it.
///
/// The farewell travels out of here rather than being sent from inside, because sending is the
/// transport's job and the caller is the one that owns a gateway. It is not optional politeness: a
/// chat that simply stops answering is what makes the uniform stranger string bearable, so the person
/// learns why from the event rather than from the silence.
/// </summary>
public sealed record TelegramUnlinkOutcome(bool Unlinked, long? FarewellChatId, string? FarewellText);

/// <summary>
/// The panel's half of account linking, and the server-side half of the bot's.
///
/// Every method takes the panel user or the Telegram sender explicitly. Nothing here reads an
/// ambient identity, for the reason <see cref="ITelegramIdentityReader"/> gives: half of these calls
/// arrive with no session at all.
/// </summary>
public interface ITelegramLinkService
{
    Task<TelegramLinkState> DescribeAsync(Guid appUserId, CancellationToken cancellationToken);

    /// <summary>Leg one: the panel mints a token and hands over the deep link, once.</summary>
    Task<TelegramLinkStart> StartAsync(Guid appUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Leg two: <c>/start &lt;token&gt;</c> arrived at the bot. This records who presented it and
    /// issues the six digits. <b>It binds nothing</b> — possession of the deep link alone gets a
    /// stranger a code and no more, because finishing requires reaching the settings page of the
    /// account being bound.
    /// </summary>
    Task<TelegramStartOutcome> PresentAsync(
        TelegramStartRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leg three, and the only one that writes a binding: the authenticated, antiforgery-protected
    /// POST from the customer's own settings page.
    /// </summary>
    Task<TelegramConfirmOutcome> ConfirmAsync(
        Guid appUserId,
        string? code,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the identity mapping and nothing else. No file is touched, no link is revoked, and
    /// nothing the customer created goes away.
    /// </summary>
    Task<TelegramUnlinkOutcome> UnlinkAsync(
        Guid appUserId,
        TelegramUnlinkReason reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes link requests that can no longer be finished. Returns how many rows went, because a
    /// sweeper that deletes nothing must not be indistinguishable from one that had nothing to do.
    /// </summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}
