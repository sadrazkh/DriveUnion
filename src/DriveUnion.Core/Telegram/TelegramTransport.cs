using System.Diagnostics.CodeAnalysis;

namespace DriveUnion.Core.Telegram;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// The wire, as types. Nothing in this file knows JSON, HTTP or a bot token: it is the shape the
// gateway seam speaks in, so that every test in the suite can drive a hand-written Telegram and the
// one implementation that reaches a socket is the only thing not covered.
// ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Why a call did not work, in Telegram's own words.
///
/// <see cref="Description"/> is kept verbatim and never paraphrased: it is the only diagnosis
/// available, and a classifier written from guesses rather than from real log lines is worth less
/// than the string it replaces.
/// </summary>
public sealed record TelegramFailure(int? ErrorCode, string Description, TimeSpan? RetryAfter = null)
{
    /// <summary>
    /// Flood control. It is <b>obeyed and not retried</b>: the item parks until the instant Telegram
    /// named and does not spend an attempt, because a backlog that exhausts its retry budget on flood
    /// control fails for a reason no user could understand.
    /// </summary>
    public bool IsFloodControl => RetryAfter is not null || ErrorCode == 429;

    /// <summary>The customer blocked the bot. The fix is on their phone and nowhere else.</summary>
    public bool IsBotBlocked =>
        ErrorCode == 403
        && Description.Contains("blocked", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Telegram account is gone. Nothing will ever be delivered to it again.</summary>
    public bool IsUserDeactivated =>
        ErrorCode == 403
        && Description.Contains("deactivated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A refusal no retry can fix. Everything else is retried with backoff, on the principle that a
    /// retried 503 costs nothing and an unretried one costs somebody's file.
    /// </summary>
    public bool IsPermanent =>
        IsBotBlocked || IsUserDeactivated || (ErrorCode is >= 400 and < 500 && !IsFloodControl);
}

/// <summary>
/// The answer to one Bot API call: a value, or a failure, never both and never neither.
///
/// It exists instead of exceptions for ordinary refusals because every caller here has to <em>do</em>
/// something with a 429 or a 403 — park, mark the account blocked, edit a card — and a thrown
/// exception is the shape that makes "and then retry for ever" the accidental default.
/// </summary>
public sealed class TelegramCall<T> where T : class
{
    private TelegramCall(T? value, TelegramFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public TelegramFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Ok => Failure is null;

    public static TelegramCall<T> Success(T value) => new(value, null);

    public static TelegramCall<T> Failed(TelegramFailure failure) => new(null, failure);

    public static TelegramCall<T> Failed(int? errorCode, string description) =>
        new(null, new TelegramFailure(errorCode, description));
}

/// <summary>A call that returns nothing but whether it worked.</summary>
public sealed record TelegramAck
{
    public static readonly TelegramAck Instance = new();
}

/// <summary>A chat, and the only thing about it this product cares about: whether it is private.</summary>
public sealed record TelegramChat(long Id, string Type)
{
    public const string PrivateType = "private";

    public bool IsPrivate => string.Equals(Type, PrivateType, StringComparison.Ordinal);
}

/// <summary>
/// Who sent an update. <see cref="Id"/> is <c>from.id</c> and never <c>chat.id</c>: the two are equal
/// in a private chat and different everywhere else, and binding on the chat would let a group's id
/// become bound.
/// </summary>
public sealed record TelegramSender(
    long Id,
    string? Username,
    string? DisplayName,
    string? LanguageCode);

/// <summary>
/// A document, video, audio or photo the customer sent the bot.
///
/// <c>FileSize</c> is optional in the API, so absent means unknown — and it is a <em>claim</em> from a
/// third party either way, which is why the ceiling is also enforced with a byte counter on the copy.
/// </summary>
public sealed record TelegramIncomingFile(
    string FileId,
    string FileUniqueId,
    string? FileName,
    string? MimeType,
    long? FileSize);

public sealed record TelegramIncomingMessage(
    long MessageId,
    TelegramChat Chat,
    TelegramSender? From,
    string? Text,
    TelegramIncomingFile? File);

public sealed record TelegramCallbackQuery(
    string Id,
    TelegramSender From,
    TelegramChat? Chat,
    long? MessageId,
    string? Data);

/// <summary>One update, reduced to the two kinds this bot answers.</summary>
public sealed record TelegramUpdate(
    long UpdateId,
    TelegramIncomingMessage? Message,
    TelegramCallbackQuery? CallbackQuery);

/// <summary>
/// An inline button. <see cref="CallbackData"/> is 1–64 bytes and is <b>never an authorization</b> —
/// it comes back from a client we do not control, so every id in it is re-resolved through a
/// tenant-scoped repository before anything happens.
/// </summary>
public sealed record TelegramInlineButton(string Text, string CallbackData);

public sealed record TelegramKeyboard(IReadOnlyList<IReadOnlyList<TelegramInlineButton>> Rows)
{
    /// <summary>One button per row, which is what a phone renders legibly for long Persian labels.</summary>
    public static TelegramKeyboard Stacked(params TelegramInlineButton[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        var rows = new List<IReadOnlyList<TelegramInlineButton>>(buttons.Length);
        foreach (var button in buttons) rows.Add([button]);

        return new TelegramKeyboard(rows);
    }

    /// <summary>Rows exactly as given, for the file card's two-by-two arrangement.</summary>
    public static TelegramKeyboard Grid(params TelegramInlineButton[][] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var built = new List<IReadOnlyList<TelegramInlineButton>>(rows.Length);
        foreach (var row in rows)
        {
            if (row.Length > 0) built.Add(row);
        }

        return new TelegramKeyboard(built);
    }
}

public sealed record TelegramOutgoingMessage(long ChatId, string Text, TelegramKeyboard? Keyboard = null);

public sealed record TelegramMessageEdit(
    long ChatId,
    long MessageId,
    string Text,
    TelegramKeyboard? Keyboard = null);

/// <summary>
/// A document on its way out. Exactly one of <see cref="CachedFileId"/> and a content stream is used:
/// with a cached handle nothing is read out of storage at all.
/// </summary>
public sealed record TelegramDocumentSend(
    long ChatId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string? CachedFileId = null,
    string? Caption = null,
    TelegramKeyboard? Keyboard = null);

/// <summary>
/// What Telegram accepted. <see cref="FileId"/> is set when a document was uploaded, and caching it is
/// the single largest performance decision in this slice.
/// </summary>
public sealed record TelegramSentMessage(
    long ChatId,
    long MessageId,
    string? FileId = null,
    string? FileUniqueId = null);

public sealed record TelegramBotProfile(long BotUserId, string Username);

/// <summary>
/// <c>getWebhookInfo</c>, which is mostly Telegram telling us why it cannot reach us. Rendering
/// <see cref="LastErrorMessage"/> verbatim is the single most useful thing on the operator's screen.
/// </summary>
public sealed record TelegramWebhookInfo(
    string? Url,
    int PendingUpdateCount,
    string? LastErrorMessage,
    DateTimeOffset? LastErrorDate,
    string? IpAddress,
    int? MaxConnections);

/// <summary>
/// Where the bytes of an inbound file are, which is not the same kind of thing on the two servers.
///
/// <para>Against the cloud API it is a URL that carries the bot token in its path — the same class of
/// secret as a resumable session URI, so it is never logged, never persisted and never put in a
/// response. Against our own server it is an absolute path on this box's disk, which is not a secret
/// of that kind but is still never rendered to anyone, because it names the working directory and
/// nobody outside this process has any use for that.</para>
///
/// <para>Both branches are live and both are tested. Production runs one and development runs the
/// other, and a branch that is only a comment about the other is a branch that has never worked.</para>
/// </summary>
public abstract record TelegramFileLocation
{
    private TelegramFileLocation()
    {
    }

    /// <summary>The local server's answer: the file already exists here, and deleting it is ours to do.</summary>
    public sealed record OnDisk(string Path) : TelegramFileLocation;

    /// <summary>The cloud API's answer: a perishable URL carrying the bot token.</summary>
    public sealed record AtUrl(Uri Url) : TelegramFileLocation;
}

/// <summary>A file Telegram is willing to hand over, and how big it says it is.</summary>
public sealed record TelegramFileHandle(TelegramFileLocation Location, long? SizeBytes);

/// <summary>What is sent while a long upload runs, so the chat does not look like a dead bot.</summary>
public static class TelegramChatActions
{
    public const string UploadDocument = "upload_document";
}
