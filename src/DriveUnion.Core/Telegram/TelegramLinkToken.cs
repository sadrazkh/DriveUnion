namespace DriveUnion.Core.Telegram;

/// <summary>
/// One half-finished binding: the panel user asked to link, and the token that proves the deep link
/// they were handed is the one the bot was shown.
///
/// <para><b>Only hashes are stored.</b> <see cref="TokenHash"/> is SHA-256 of the 32 random bytes
/// that ride in the deep link, and <see cref="ConfirmationCodeHash"/> is SHA-256 of the six digits
/// salted with <see cref="Id"/>. This table is otherwise a table of live credentials, and a database
/// dump, a support query pasted into a ticket or a logged result set must not be a set of working
/// keys — the same reasoning that put the Google refresh tokens behind Data Protection. SHA-256
/// rather than a password hash because the token is 256 bits of entropy with nothing to brute-force
/// and the lookup has to be one indexed read; the six digits are not brute-forceable either, because
/// <see cref="Attempts"/> is capped long before 10⁶.</para>
///
/// <para>The row survives its own consumption. <see cref="ConsumedAt"/> is what makes the binding
/// single-use, and it is set by the same conditional statement that inserts the
/// <see cref="TelegramAccount"/>.</para>
/// </summary>
public sealed class TelegramLinkToken
{
    /// <summary>
    /// Five wrong codes and the token is dead.
    ///
    /// The code is not the primary control — the binding is written by an authenticated,
    /// antiforgery-protected POST from the settings page of the account being bound — so a short
    /// budget costs a legitimate customer nothing and removes the only guessable value in the flow.
    /// </summary>
    public const int MaxAttempts = 5;

    public Guid Id { get; set; }

    public Guid AppUserId { get; set; }

    public required string TokenHash { get; set; }

    /// <summary>Null until the bot has been shown the deep link. Nothing can be confirmed before then.</summary>
    public string? ConfirmationCodeHash { get; set; }

    /// <summary>
    /// Who opened the deep link. Written on the bot's leg and read on the panel's, which is what
    /// makes the binding land on the Telegram account that actually received the six digits.
    /// </summary>
    public long? PresentedTelegramUserId { get; set; }

    public long? PresentedChatId { get; set; }

    public DateTimeOffset? PresentedAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
