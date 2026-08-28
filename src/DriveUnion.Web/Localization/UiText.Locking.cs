namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// Locking a file that is already stored.
    ///
    /// <para>The words matter more here than on most screens, because what the button does is
    /// irreversible in one direction: after it, the only way back to a readable copy is the
    /// passphrase. Somebody who does not understand that before they press it will find out by
    /// losing a file, so the card says it in a sentence rather than implying it with a padlock.</para>
    /// </summary>
    public static class Locking
    {
        public static string Title => Pick("قفل‌کردن این فایل", "Lock this file");

        /// <summary>
        /// What it does, and the part that cannot be undone.
        ///
        /// <para>«نسخهٔ خوانا پاک می‌شود» is the whole warning. Everything else on this card is
        /// mechanics; this is the sentence somebody has to have read.</para>
        /// </summary>
        public static string Explain => Pick(
            "فایل با رمزی که می‌دهید رمزگذاری می‌شود و نسخهٔ خوانا از فضای ذخیره‌سازی پاک می‌شود. "
            + "بعد از آن، تنها راه باز کردنش همین رمز است — اگر گمش کنید ما هم نمی‌توانیم بازش کنیم.",
            "The file is encrypted with the passphrase you give, and the readable copy is deleted "
            + "from storage. After that the passphrase is the only way to open it — if you lose it, "
            + "we cannot open it either.");

        /// <summary>Said where the passphrase is typed, because it is the one thing to get right.</summary>
        public static string SecretLabel => Pick("رمز", "Passphrase");

        public static string Start => Pick("قفل کن", "Lock it");

        public static string Working => Pick("در حال قفل‌کردن…", "Locking…");

        /// <summary>
        /// What happens to the links, said before it happens.
        ///
        /// <para>A link handed out as «click and it downloads» that silently becomes «type a
        /// passphrase nobody gave you» is worse than one that has stopped working: the second is a
        /// thing the sender can be told about, and the first is one the recipient blames themselves
        /// for. So the links are revoked, and the owner is told that here rather than discovering it
        /// when somebody asks why their link is dead.</para>
        /// </summary>
        public static string LinksWillStop => Pick(
            "لینک‌های عمومی فعلی این فایل باطل می‌شوند. بعد از قفل‌شدن می‌توانید با «کلید لینک» دوباره به اشتراک بگذارید.",
            "Any public links this file has will stop working. Once it is locked you can share it "
            + "again with a link key.");

        /// <summary>Said only where the room is genuinely needed, i.e. next to the button.</summary>
        public static string NeedsRoom => Pick(
            "تا وقتی کار تمام شود، فایل دو بار جا می‌گیرد.",
            "Until it finishes, the file takes up its space twice.");

        public static string Queued => Pick(
            "در صف قفل‌شدن قرار گرفت. می‌توانید صفحه را ببندید.",
            "Queued. You can close the page.");

        // ── what a row says while it is happening ───────────────────────────────────────────────

        public static string StatusPending => Pick("در صف", "Queued");

        public static string StatusRunning => Pick("در حال قفل‌شدن", "Locking");

        public static string StatusFailed => Pick("انجام نشد", "Did not finish");

        // ── refusals, each one something the customer can act on ────────────────────────────────

        public static string RefusalUnknownFile => Pick(
            "این فایل پیدا نشد.",
            "That file was not found.");

        public static string RefusalAlreadyLocked => Pick(
            "این فایل از قبل قفل است.",
            "That file is already locked.");

        public static string RefusalAlreadyLocking => Pick(
            "این فایل همین حالا در حال قفل‌شدن است.",
            "That file is already being locked.");

        /// <summary>
        /// The one refusal that is about a number rather than a state, so it says what the number is
        /// about: locking is a copy before it is a replacement.
        /// </summary>
        public static string RefusalNoRoom => Pick(
            "برای نسخهٔ دوم جا نیست. قفل‌کردن تا وقتی تمام شود به اندازهٔ خود فایل جای بیشتری می‌خواهد.",
            "There is no room for the second copy. Until it finishes, locking needs as much space "
            + "again as the file itself.");

        public static string RefusalMalformed => Pick(
            "درخواست درست نبود. صفحه را تازه کنید و دوباره امتحان کنید.",
            "That request was not right. Refresh the page and try again.");

        public static string Failed => Pick(
            "قفل‌کردن انجام نشد. فایل شما دست‌نخورده است.",
            "Locking did not finish. Your file is untouched.");
    }
}
