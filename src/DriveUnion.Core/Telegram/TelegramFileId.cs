namespace DriveUnion.Core.Telegram;

/// <summary>
/// The free lunch: once Telegram has the bytes, re-sending them is one API call with a 64-byte
/// argument.
///
/// <para>A first delivery of a ceiling-sized file is a read out of the storage pool, a multipart
/// upload, minutes of a transfer slot and a full copy on a disk that has no room. Every delivery after
/// it is <c>sendDocument</c> with this string: no size limit, no bytes leaving the box, no storage
/// read, no egress, no daily-quota consumption, and — the part that matters most here — no disk. It is
/// also what makes deleting the local copy the instant a send returns safe rather than reckless,
/// because the value in that same response is a permanent handle to the bytes.</para>
///
/// <para><b>Keyed on the bot as well as the file, and that is a correctness requirement rather than a
/// nicety.</b> A <c>file_id</c> is unique per bot and cannot be transferred to another one, so
/// pointing the panel at a different token must produce a cache <em>miss</em> and never a wrong send.
/// The same property is what lets a migration between Bot API servers truncate this table rather than
/// reason about whether the old values still work: a miss costs one re-upload and can never cost a
/// file, because the file is in the storage pool and always was.</para>
/// </summary>
public sealed class TelegramFileId
{
    public Guid StoredFileId { get; set; }

    /// <summary>The bot the value was minted against — the digits before the colon in its token.</summary>
    public long BotUserId { get; set; }

    public required string FileId { get; set; }

    /// <summary>
    /// Telegram's stable-across-bots identifier. It cannot be used to send anything, which is why it
    /// is not the key; it is kept because it is the only way to notice that two files are the same
    /// bytes.
    /// </summary>
    public required string FileUniqueId { get; set; }

    /// <summary>What was uploaded, so a file replaced under the same row is not re-sent from a stale handle.</summary>
    public long SizeBytes { get; set; }

    public DateTimeOffset CachedAt { get; set; }
}
