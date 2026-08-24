namespace DriveUnion.Core.Telegram;

/// <summary>Where updates come from. One key decides, and both are implemented.</summary>
public enum TelegramUpdateSource
{
    /// <summary>
    /// <c>getUpdates</c> in a loop. What development runs, because this machine has no public HTTPS
    /// and no local Bot API server to POST at it.
    /// </summary>
    Polling = 0,

    /// <summary>
    /// Telegram — or, in production, our own Bot API server over loopback — POSTs to us. What the
    /// deployed box runs.
    /// </summary>
    Webhook = 1,
}

/// <summary>
/// Everything about the transport that differs between this machine and the box, in one place.
///
/// <para><b>The two size ceilings are configuration and never constants in code, and that is
/// load-bearing rather than tidy.</b> Development talks to <c>api.telegram.org</c>, where a bot may
/// upload 50 MB and download 20 MB; production talks to our own <c>telegram-bot-api --local</c>, where
/// it may upload 2000 MB and the inbound ceiling is a product decision rather than an API one. The two
/// environments differ in these numbers on purpose, so no code path anywhere may know what they
/// are — and the day the box turns out to have less disk than the arithmetic in the design needs, the
/// answer is a smaller ceiling here rather than a redesign.</para>
///
/// <para><see cref="ApiBaseUrl"/> and <see cref="LocalBotServer"/> are deliberately <em>not</em> rows
/// in <c>TelegramBotSettings</c>. An operator who can repoint production at the cloud API from a web
/// form is an operator who can do it by accident, and the recovery from that is a ten-minute
/// irreversible <c>logOut</c> door rather than a second click.</para>
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>
    /// A message may be deleted only within 48 hours of being sent, so a longer lifetime is a timer
    /// that silently never fires. Anything above this is refused when the options are validated
    /// rather than truncated at run time.
    /// </summary>
    public const int MaxDeliveryMessageTtlMinutes = 2820;

    /// <summary>
    /// Where the Bot API lives. <c>https://api.telegram.org</c> in development, and
    /// <c>http://127.0.0.1:8081</c> on the box.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>
    /// True when <see cref="ApiBaseUrl"/> is our own server running with <c>--local</c>.
    ///
    /// <para>It changes exactly one thing in the code and it is not a size: <c>getFile</c> returns an
    /// absolute path on this box's disk rather than a URL to fetch, which means the bytes already
    /// exist here and deleting them is our obligation. Both shapes are implemented and both are
    /// tested, because production runs one and development runs the other.</para>
    /// </summary>
    public bool LocalBotServer { get; set; }

    public TelegramUpdateSource UpdateSource { get; set; } = TelegramUpdateSource.Polling;

    /// <summary>
    /// Decimal bytes. The default is the cloud API's 50 MB, because that is what an unconfigured
    /// deployment is actually talking to; production sets <c>2_000_000_000</c>.
    /// </summary>
    public long MaxSendBytes { get; set; } = 50_000_000;

    /// <summary>Decimal bytes. The cloud API's 20 MB by default; <c>2_000_000_000</c> on the box.</summary>
    public long MaxReceiveBytes { get; set; } = 20_000_000;

    /// <summary>
    /// Who may POST an update at us. Empty means "anyone who has the secret", which is what
    /// development against the cloud API has to accept: Telegram's own documented subnets are
    /// explicitly subject to change, and the box sits behind a proxy whose forwarded headers have to
    /// be trusted correctly for a source address to mean anything at all.
    ///
    /// <para>Against our own server it becomes a real control with one entry — <c>127.0.0.1</c> — that
    /// will never change, and nginx is not in the path so there is no forwarded header to get wrong.
    /// The secret token stays primary either way, because a second process on the box also arrives
    /// from loopback.</para>
    /// </summary>
    public IList<string> TrustedSubnets { get; } = [];

    /// <summary>At most this many queued items per tenant before the bot says the queue is full.</summary>
    public int MaxQueuedPerTenant { get; set; } = 50;

    /// <summary>
    /// The second bound, and the one that matters at a 2000 MB ceiling: fifty queued items was
    /// 2.5 GB of pending work at 50 MB and is 100 GB at 2000 MB, which is days. A bound in items only
    /// is not a bound.
    /// </summary>
    public long MaxQueuedBytesPerTenant { get; set; } = 20L * 1000 * 1000 * 1000;

    /// <summary>
    /// How many outbox items that move bytes may run at once. Short items — text, cards, callback
    /// answers, message deletions — are never counted against it and are never blocked behind one,
    /// because the chat replies that explain what is happening must not be stuck behind the transfers
    /// causing it.
    /// </summary>
    public int MaxConcurrentTransfers { get; set; } = 2;

    /// <summary>Attempts for an item that costs one request.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Attempts for an item that costs its own size twice over — once read out of Drive, once pushed
    /// to the server. Five attempts on a failing 2 GB delivery is twenty gigabytes of reads and
    /// egress for something that has already failed four times.
    /// </summary>
    public int MaxTransferAttempts { get; set; } = 3;

    /// <summary>
    /// Zero — never — and that is the default on purpose. The delivered document sitting in the
    /// customer's own chat is the one genuine second copy in this design, and a timer measured in
    /// minutes deletes it. The «دریافت کردم، پاک کن» button is the feature; this is the opt-in.
    ///
    /// <para>Values above <see cref="MaxDeliveryMessageTtlMinutes"/> are refused when the options are
    /// validated at startup rather than clamped at run time. Clamping is how a number nobody meant
    /// becomes the number in production.</para>
    /// </summary>
    public int DeliveryMessageTtlMinutes { get; set; }

    /// <summary>
    /// The Bot API server's working directory, which is where every byte it handles lands. Null in
    /// development, where there is no local server and therefore no directory.
    ///
    /// <para>The path swept is <c>&lt;WorkDirectory&gt;/&lt;bot user id&gt;/</c> — the server organises
    /// files per bot, keyed by the bot's numeric id.</para>
    /// </summary>
    public string? WorkDirectory { get; set; }

    /// <summary>
    /// Free space kept spare on top of the file itself before a byte-moving operation is allowed to
    /// start. Beginning a 2 GB transfer onto a volume that cannot hold 2 GB fails at 98%, having done
    /// all the work and read all the bytes out of Drive.
    /// </summary>
    public long WorkDirHeadroomBytes { get; set; } = 1_000_000_000;

    /// <summary>
    /// How old a file in the working directory has to be before the sweeper takes it. Comfortably
    /// past the longest legitimate hold, which is one ceiling-sized transfer and its retries.
    /// </summary>
    public int WorkDirMaxAgeMinutes { get; set; } = 30;

    /// <summary>
    /// Below this the sweeper deletes oldest-first regardless of age and no new byte-moving work is
    /// accepted. Deleting a five-minute-old file is destructive — it may be an in-flight transfer,
    /// which will then fail — and that is the correct trade: a failed transfer is one error message,
    /// a full volume takes the database and the upload spool down with it.
    /// </summary>
    public long WorkDirMinFreeBytes { get; set; } = 2_000_000_000;

    /// <summary>How long <c>getUpdates</c> holds the connection open. Telegram's own long poll.</summary>
    public int PollTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Left at the cloud API's default. The local server permits a hundred thousand, which is an
    /// invitation to point that many concurrent POSTs at one Kestrel process on a box with no disk;
    /// a configuration key whose maximum is that far outside anything sane is a hazard, not a feature.
    /// </summary>
    public int MaxWebhookConnections { get; set; } = 40;

    /// <summary>
    /// How many replies an unbound sender gets in an hour. Beyond it the update is consumed and
    /// <b>nothing is sent</b> — silence rather than an error, because an error is a reply and a reply
    /// is the resource being abused.
    /// </summary>
    public int StrangerRepliesPerHour { get; set; } = 3;

    /// <summary>
    /// Where the panel lives, so a refusal can point at the uploader that will accept what the bot
    /// cannot carry. Filled from <c>DriveUnion:PublicBaseUrl</c> when it is not set here.
    /// </summary>
    public string? PanelBaseUrl { get; set; }

    /// <summary>
    /// How long the drainer waits between claims when it found nothing to do.
    /// </summary>
    public int DrainIntervalSeconds { get; set; } = 2;

    /// <summary>Between sweeps of the working directory. Every minute, not nightly: a directory that
    /// can gain two gigabytes per message is not swept by a nightly job.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>The per-bot subdirectory the local server writes into, or null when there is none.</summary>
    public string? WorkDirectoryFor(long? botUserId) =>
        WorkDirectory is { Length: > 0 } root && botUserId is { } id
            ? Path.Combine(root, id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
}
