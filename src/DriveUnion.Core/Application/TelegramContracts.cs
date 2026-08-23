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

/// <summary>
/// What the bot can be asked to do from this side. One method, because linking is the only thing in
/// this slice that has to reach a chat.
///
/// <para>This is the seam the transport plugs into, and nothing here implements Telegram. The only
/// implementation that ships with this slice reports that it delivered nothing, which is the truth
/// until a transport exists — see <c>UnconfiguredTelegramBotGateway</c>.</para>
/// </summary>
public interface ITelegramBotGateway
{
    /// <summary>False when the message did not reach Telegram. Never throws for an ordinary failure.</summary>
    Task<bool> TrySendMessageAsync(long chatId, string text, CancellationToken cancellationToken);
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
    DateTimeOffset? UpdatedAt);

public interface ITelegramBotSettingsStore
{
    /// <summary>What the panel may see.</summary>
    Task<StoredTelegramBot> ReadAsync(CancellationToken cancellationToken);

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
public sealed record TelegramOperatorHealth(int LinkedAccounts, int PendingRequests);

public interface ITelegramOperatorView
{
    Task<TelegramOperatorHealth> ReadAsync(CancellationToken cancellationToken);
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
