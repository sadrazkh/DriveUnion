namespace DriveUnion.Core.Telegram;

/// <summary>What a queued item is going to do when the drainer reaches it.</summary>
public enum TelegramOutboxKind : byte
{
    /// <summary>One text message. Costs a request.</summary>
    SendMessage = 10,

    /// <summary>
    /// A file out of the tenant's storage and into the chat. This is the one that costs its own size
    /// twice over, holds a transfer slot for minutes, and can fill the volume.
    /// </summary>
    SendDocument = 20,

    /// <summary>
    /// A file the customer sent the bot, on its way into their storage. Byte-moving in the other
    /// direction, and subject to the same slot, the same pre-flight and the same attempt budget.
    /// </summary>
    ReceiveDocument = 30,

    /// <summary>
    /// «دریافت کردم، پاک کن», or an armed lifetime that has come due.
    ///
    /// <para>It is a queued row rather than an in-memory timer because a deploy restarts the process
    /// and an in-memory timer does not survive one — and «پیامش پاک بشه» failing silently after a
    /// release is exactly the class of bug this design keeps refusing to ship. The queue is the
    /// timer.</para>
    /// </summary>
    DeleteMessage = 40,
}

public enum TelegramOutboxStatus : byte
{
    Pending = 10,

    /// <summary>Claimed by a drainer and in flight.</summary>
    Claimed = 20,

    Sent = 30,

    /// <summary>The attempt budget is gone, or the failure is one no retry can fix.</summary>
    Failed = 40,
}

/// <summary>
/// The rest of what an item needs, as one JSON column.
///
/// It is a payload rather than six nullable columns because the four kinds want different things and
/// a nullable column that only one kind ever writes is a column somebody eventually misreads as
/// "not set yet".
/// </summary>
public sealed record TelegramOutboxPayload
{
    /// <summary>The message body, for a plain send.</summary>
    public string? Text { get; init; }

    /// <summary>Which message to delete, for a deletion.</summary>
    public long? MessageId { get; init; }

    /// <summary>
    /// The card the request came from, so a long transfer edits the message it started from rather
    /// than appending new ones. Null when the item was not started from a card.
    /// </summary>
    public long? CardMessageId { get; init; }

    /// <summary>Telegram's handle for an inbound file, which is what <c>getFile</c> takes.</summary>
    public string? TelegramFileId { get; init; }

    public string? FileName { get; init; }

    public string? MimeType { get; init; }
}

/// <summary>
/// One thing the bot owes a chat.
///
/// <para><b><see cref="TenantId"/> is not nullable, and that is the whole of the drainer's tenant
/// identity.</b> The drainer runs with no request, no cookie and no principal; this column is the only
/// thing that says whose files an item may read. There is no system-owned outbox item and there must
/// never be one, because the code that handles it would be code with no tenant.</para>
///
/// <para><see cref="SentMessageId"/> exists rather than being derived because it is what makes
/// deletion possible: a <see cref="TelegramOutboxKind.SendDocument"/> item records the message id
/// Telegram returned, and the <see cref="TelegramOutboxKind.DeleteMessage"/> item that follows carries
/// that id.</para>
/// </summary>
public sealed class TelegramOutbox
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Where it is going. In a private chat this equals the customer's Telegram user id.</summary>
    public long ChatId { get; set; }

    public TelegramOutboxKind Kind { get; set; }

    /// <summary>The file, for the two byte-moving kinds. Null for a message or a deletion.</summary>
    public Guid? StoredFileId { get; set; }

    /// <summary>
    /// Everything else the item needs, as JSON: the message text, the Telegram file id of an inbound
    /// document, the message id to delete. It is a column rather than six nullable ones because the
    /// four kinds want different things and a nullable column nobody writes is a column somebody
    /// eventually misreads.
    /// </summary>
    public string? Payload { get; set; }

    public TelegramOutboxStatus Status { get; set; } = TelegramOutboxStatus.Pending;

    public int Attempt { get; set; }

    /// <summary>
    /// When this becomes eligible. It is a backoff for a failure, a park for a flood-control
    /// <c>retry_after</c>, and a deadline for a deletion — the same column, because they are the same
    /// question.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>
    /// How many bytes this item will move, for the byte-denominated queue bound. Zero for everything
    /// that is not a transfer.
    /// </summary>
    public long SizeBytes { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>
    /// Telegram's own words, verbatim. Classifying an error into our own vocabulary throws away the
    /// only diagnosis available, and the classifier is meant to be tightened from real log lines
    /// rather than from a mapping guessed in advance.
    /// </summary>
    public string? ErrorDetail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>What Telegram called the message it accepted. Null until it has accepted one.</summary>
    public long? SentMessageId { get; set; }

    /// <summary>True for the two kinds that hold a transfer slot and a lower attempt budget.</summary>
    public bool MovesBytes => Kind is TelegramOutboxKind.SendDocument or TelegramOutboxKind.ReceiveDocument;
}
