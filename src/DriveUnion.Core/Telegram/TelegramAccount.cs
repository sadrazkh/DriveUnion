namespace DriveUnion.Core.Telegram;

/// <summary>
/// Whether Telegram will still take a message for this chat.
///
/// Nothing in this slice writes anything but <see cref="Active"/>: the other two are set from the
/// two 403s a send comes back with, and there is no send yet. The column exists now because it
/// belongs to the binding row rather than to the transport, and because the customer's settings card
/// already has to render it — a card that learns about "blocked" later would have to be redesigned
/// rather than extended.
/// </summary>
public enum TelegramDeliveryStatus : byte
{
    Active = 10,

    /// <summary>The customer blocked the bot. The fix is on their phone and nowhere else.</summary>
    Blocked = 20,

    /// <summary>The Telegram account is gone.</summary>
    Deactivated = 30,
}

/// <summary>
/// One Telegram user, bound to one panel user.
///
/// <para><b>There is deliberately no <c>TenantId</c> column here, and adding one is a security
/// change even though it compiles.</b> The tenant is read through <see cref="AppUserId"/> on every
/// update. A denormalised copy would go stale the day a user moves between workspaces or is removed
/// — and it would go stale silently, which means the bot would keep answering with the old tenant's
/// files and nothing anywhere would fail. <c>GoogleAccount</c> carries the same absence for the same
/// reason.</para>
///
/// <para><see cref="TelegramUserId"/> is the <c>from.id</c> of the sender, never the <c>chat.id</c>.
/// The two are equal in a private chat and different everywhere else, so binding on the chat would
/// let a <em>group's</em> id become bound — and then every member of that group reads the tenant's
/// files. <see cref="ChatId"/> is kept beside it because that is where a reply is addressed, and it
/// is never what an identity is resolved from.</para>
/// </summary>
public sealed class TelegramAccount
{
    public Guid Id { get; set; }

    public Guid AppUserId { get; set; }

    /// <summary>Telegram's <c>from.id</c>. Unique across the product — one panel user per Telegram account.</summary>
    public long TelegramUserId { get; set; }

    /// <summary>Where a reply is addressed. Equal to <see cref="TelegramUserId"/> in a private chat.</summary>
    public long ChatId { get; set; }

    /// <summary>The <c>@name</c>, without the at sign. Telegram users need not have one.</summary>
    public string? Username { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Telegram's <c>language_code</c>, so the bot can answer in the right language.</summary>
    public string? LanguageCode { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public TelegramDeliveryStatus DeliveryStatus { get; set; } = TelegramDeliveryStatus.Active;

    public DateTimeOffset? BlockedAt { get; set; }
}
