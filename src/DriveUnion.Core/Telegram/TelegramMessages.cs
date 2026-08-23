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
}
