namespace DriveUnion.Core.Telegram;

/// <summary>
/// One row per update Telegram has handed us, and the reason a webhook retry does not upload a
/// customer's file twice.
///
/// <para>Redelivery is not a hypothetical here, it is the documented behaviour: Telegram repeats an
/// update whenever the webhook answers non-2xx or times out. Without this table a slow handler, a
/// deploy landing mid-request or one dropped connection is a second upload into somebody's storage —
/// a real cost to a real customer, arriving as a duplicate file they did not send.</para>
///
/// <para>The write is insert-on-conflict-do-nothing, and a conflict is the answer rather than an
/// error: it means "already handled", and the correct response is 200 and stop. Anything else invites
/// the retry that produced the conflict.</para>
/// </summary>
public sealed class TelegramUpdateSeen
{
    /// <summary>Telegram's own <c>update_id</c>, and the whole of the key.</summary>
    public long UpdateId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
