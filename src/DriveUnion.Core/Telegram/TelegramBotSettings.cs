namespace DriveUnion.Core.Telegram;

/// <summary>
/// The operator's bot, in one row. There is exactly one bot for the whole product — it is the
/// operator's, like the Google pool — so there is no <c>TenantId</c> here and no second row.
///
/// <para><b>Why a row and not a file.</b> The Google OAuth client is kept in a JSON file, and
/// <c>FileGoogleOAuthCredentialStore</c>'s own comment names that as the weaker choice: the Data
/// Protection key ring survives a redeploy and the file does not unless the deployment keeps it on a
/// volume. For Google, losing it costs one re-paste on a screen that explains where to find the
/// value. For Telegram it costs more than that — a registered webhook is bound to the token, and a
/// process that has forgotten its token leaves Telegram POSTing at a URL nobody recognises with
/// nothing in any log to say so. So this one lives where the key ring lives.</para>
/// </summary>
public sealed class TelegramBotSettings
{
    /// <summary>The only id there is. Seeded by the migration so the row always exists.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The @BotFather token, encrypted with the same <c>ITokenProtector</c> that protects the Google
    /// refresh tokens. Null when the operator has saved nothing.
    /// </summary>
    public string? BotTokenProtected { get; set; }

    /// <summary>The bot's <c>@name</c> without the at sign. Every customer's deep link is built from it.</summary>
    public string? BotUsername { get; set; }

    /// <summary>
    /// The numeric id of the bot, which is the part of the token before the colon.
    ///
    /// Telegram's <c>getMe</c> is the authoritative answer and a later slice will store that one;
    /// until a transport exists there is nothing to ask, and the token carries the id in plain sight,
    /// so reading it costs no network call and no guesswork.
    /// </summary>
    public long? BotUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
