namespace DriveUnion.Core.Telegram;

/// <summary>
/// Everything the bot says during linking, spelled once.
///
/// These are here rather than inside the service because two of them are product rules rather than
/// wording: <see cref="Stranger"/> has to be byte-identical across three different situations, and
/// <see cref="Farewell"/> is what makes that acceptable. A test asserts both against these constants,
/// so a well-meaning edit that adds "your link was removed yesterday" to one branch turns red.
/// </summary>
public static class TelegramMessages
{
    /// <summary>
    /// The one answer everyone unbound gets — never linked, unlinked yesterday, or belonging to a
    /// panel user who has been removed.
    ///
    /// A bot that answered "your account was disconnected" to one stranger and "unknown account" to
    /// another is an oracle for which Telegram accounts are customers of this service, and anyone in
    /// the world can make the bot answer.
    /// </summary>
    public const string Stranger =
        "این ربات مخصوص کاربران Drive Union است. برای اتصال، از بخش تنظیمات پنل خود شروع کنید.";

    /// <summary>
    /// Sent at the moment a binding is removed, from either end.
    ///
    /// This is the price of the uniform stranger string: the person learns why from the event rather
    /// than from the steady state. A chat that simply stops answering is the failure this product
    /// keeps refusing to ship.
    /// </summary>
    public const string Farewell = "اتصال این حساب تلگرام به Drive Union قطع شد.";

    /// <summary>Everything wrong with a <c>/start</c> token, in one string: unknown, expired or spent.</summary>
    public const string TokenNotUsable =
        "این پیوند معتبر نیست یا منقضی شده است. از صفحه‌ی تنظیمات پنل، پیوند تازه‌ای بسازید.";

    public const string AlreadyBoundElsewhere =
        "این حساب تلگرام قبلاً به یک حساب دیگر متصل است.";

    /// <summary>A bound customer who sent a bare <c>/start</c>. The card itself is a later slice.</summary>
    public const string AlreadyLinked =
        "حساب تلگرام شما به Drive Union متصل است.";

    /// <summary>
    /// The six digits, and the one sentence that makes a forwarded deep link harmless.
    ///
    /// The warning is not decoration. Anyone holding the link can reach this message; the sentence is
    /// what tells them that reaching it means nothing, and tells the person who did not open it that
    /// something is wrong.
    /// </summary>
    public static string ConfirmationCode(string code) =>
        $"کد تأیید شما: {code}\n\n"
        + "این کد را در صفحه‌ی تنظیمات پنل وارد کنید تا اتصال کامل شود.\n"
        + "اگر این را از صفحه‌ی تنظیمات خودتان باز نکرده‌اید، این پیام را نادیده بگیرید.";

    // ────────────────────────────────────────────────────────────────────────────────────────────
    // The bot's surface. Few commands, one screen, and every message ends somewhere.
    //
    // Two rules govern everything below and both are tested rather than trusted:
    //
    //   1. No string here names the storage provider, an account, an address or an internal id. The
    //      customer must never learn that a pool of accounts exists, and a chat is a new way to leak
    //      it — an error string, a "which account" hint, a token-shaped value in a message.
    //   2. Every refusal names the next action, and three different failures never share one
    //      sentence. From a chat, "this file cannot be fetched right now", "you have no files" and
    //      "you are not linked" look identical if they are all «خطایی رخ داد», and this is precisely
    //      the moment a customer needs to know which one they are looking at.
    // ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Group chats are refused with one line and nothing else happens.</summary>
    public const string PrivateChatsOnly = "این ربات فقط در گفتگوی خصوصی کار می‌کند.";

    public const string Home =
        "خوش آمدید.\n\n"
        + "• فایل‌هایتان را با /files ببینید و از همان‌جا برایتان فرستاده شود.\n"
        + "• برای بارگذاری، کافی است فایل را همین‌جا بفرستید.\n"
        + "• فضای مصرفی را با /quota ببینید.";

    /// <summary>
    /// The ceilings are in <c>/help</c> on purpose: they are the one thing a customer will otherwise
    /// discover by failing, and a bot that states its limits before it is asked is the difference
    /// between «شکیل» and broken.
    /// </summary>
    public static string Help(string maxSend, string maxReceive) =>
        "این ربات راه دومی برای رسیدن به فایل‌های شماست، وقتی باز کردن پنل ممکن نیست.\n\n"
        + $"• فایل تا {maxSend} را می‌تواند همین‌جا برایتان بفرستد؛ بزرگ‌تر از آن با لینک فرستاده می‌شود.\n"
        + $"• فایل تا {maxReceive} را می‌تواند از شما بگیرد؛ بزرگ‌تر از آن را از بارگذارِ پنل بفرستید.\n\n"
        + "دستورها: /files · /quota · /unlink";

    public const string NoFiles =
        "هنوز فایلی ندارید. یک فایل را همین‌جا بفرستید تا ذخیره شود، یا از بارگذارِ پنل استفاده کنید.";

    /// <summary>
    /// A file that is not this tenant's, and a file that never existed, get the same card. A
    /// distinguishable "not yours" is what turns an id into something worth guessing.
    /// </summary>
    public const string FileNotAvailable =
        "این فایل در دسترس نیست. فهرست فایل‌هایتان را با /files ببینید.";

    /// <summary>
    /// The storage read failed. It is deliberately not the same sentence as «فایلی ندارید» or as the
    /// stranger reply: the customer's next action is to wait and try again, and only this one says so.
    /// </summary>
    public const string TemporarilyUnavailable =
        "الان نمی‌شود این فایل را آورد. چند دقیقه‌ی دیگر دوباره تلاش کنید.";

    /// <summary>M2's tenant-facing refusal, never the operator one that names blocked accounts.</summary>
    public const string UploadUnavailable = "آپلود موقتاً در دسترس نیست.";

    public const string QueueFull =
        "چند درخواست در صف دارید — پس از اتمام دوباره تلاش کنید.";

    /// <summary>
    /// Deliberately not «خطا». The item is still queued and will run; saying otherwise would invite a
    /// second press that queues the same two gigabytes again.
    /// </summary>
    public const string DiskBusy =
        "الان جا برای این انتقال نیست. در صف می‌ماند و به‌زودی فرستاده می‌شود.";

    public const string Queued = "در صف ارسال قرار گرفت.";

    public const string Preparing = "در حال آماده‌سازی…";

    public const string Delivered = "فرستاده شد.";

    public const string DeliveryFailed =
        "فرستادن این فایل انجام نشد. می‌توانید دوباره تلاش کنید یا با لینک بفرستید.";

    /// <summary>The second line of a card for a file the bot cannot carry. It does not apologise.</summary>
    public const string TooLargeToSend = "بزرگ‌تر از سقف تلگرام — با لینک بفرستید";

    /// <summary>
    /// A locked file, which this bot cannot send and must not appear to.
    ///
    /// <para>It names the remedy because there is one: the public link page asks for the key and does
    /// the unlocking in the visitor's own browser, which is the only place it can happen. Neither
    /// this process nor Telegram has anything to unlock it with, and a document delivered into a chat
    /// as ciphertext would be a file nobody discovers is unreadable until they try to open it.</para>
    /// </summary>
    public const string FileIsLocked =
        "این فایل قفل است و ربات نمی‌تواند بازش کند. با لینک بفرستید — گیرنده رمز را در مرورگر خودش وارد می‌کند.";

    public static string InboundTooLarge(string ceiling, string uploaderUrl) =>
        $"این فایل بزرگ‌تر از {ceiling} است و از راه تلگرام نمی‌آید.\n"
        + $"از بارگذارِ پنل بفرستید: {uploaderUrl}";

    public const string InboundReceived = "فایل ذخیره شد.";

    public const string InboundFailed =
        "ذخیره‌ی این فایل انجام نشد. دوباره بفرستید یا از بارگذارِ پنل استفاده کنید.";

    public static string InboundAccepted(string name) => $"«{name}» دریافت شد؛ در حال ذخیره…";

    public static string LinkCreated(string url) =>
        $"لینک ساخته شد:\n{url}\n\nهر کسی این نشانی را داشته باشد می‌تواند فایل را بگیرد.";

    public const string LinkFailed = "ساخت لینک انجام نشد. کمی بعد دوباره تلاش کنید.";

    public const string UnlinkConfirm =
        "با قطع اتصال، این ربات دیگر به فایل‌های شما دسترسی ندارد. فایل‌ها و لینک‌هایتان دست‌نخورده می‌مانند.";

    public const string Cancelled = "لغو شد.";

    /// <summary>
    /// Said only when a lifetime is actually armed. A message that disappears without warning is the
    /// same failure as a chat that stops answering.
    /// </summary>
    public static string DeletesIn(int minutes) =>
        $"این پیام تا {TelegramFormats.Digits(minutes)} دقیقه‌ی دیگر پاک می‌شود.";

    public static string Quota(string used, string files) =>
        $"فضای مصرفی: {used}\nتعداد فایل‌ها: {files}";

    // Button labels.
    public const string ButtonSend = "ارسال فایل";
    public const string ButtonCreateLink = "ساخت لینک";
    public const string ButtonFiles = "فایل‌ها";
    public const string ButtonMore = "بیشتر";
    public const string ButtonAcknowledge = "دریافت کردم، پاک کن";
    public const string ButtonRetry = "تلاش دوباره";
    public const string ButtonConfirmUnlink = "بله، قطع کن";
    public const string ButtonCancel = "انصراف";
}
