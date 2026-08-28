namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// Watching a file from the panel instead of downloading it.
    ///
    /// <para>Few words, because the control is a play button and a play button does not need
    /// explaining. The ones that exist are all for the locked case, where something can go wrong in
    /// a way the reader has to be able to tell apart: a passphrase that is not the right one, and a
    /// browser that cannot do this at all.</para>
    /// </summary>
    public static class Player
    {
        public static string Play => Pick("پخش", "Play");

        /// <summary>The control on the file's own card, which opens the page rather than playing here.</summary>
        public static string Watch => Pick("تماشا", "Watch");

        public static string SecretLabel => Pick("رمز این فایل", "This file's passphrase");

        /// <summary>
        /// The heading on the unlock card, which is the whole page until the passphrase is right.
        /// </summary>
        public static string LockedTitle => Pick("این فایل قفل است", "This file is locked");

        /// <summary>
        /// Said under it, and it is the sentence that stops the page reading as a paywall.
        ///
        /// <para>Somebody arriving here has pressed «تماشا» on a file and met a password box. Without
        /// a reason that is indistinguishable from being asked to log in again, so it says where the
        /// unlocking happens and what that buys — which is also the product's central claim, at the
        /// one moment it is doing something visible.</para>
        /// </summary>
        public static string LockedHint => Pick(
            "رمزش را بزنید تا همین‌جا در مرورگر خودتان باز شود و پخش شود. نسخهٔ بازشده جایی ذخیره نمی‌شود.",
            "Enter the passphrase and it is opened here, in your own browser, as it plays. No "
            + "readable copy is stored anywhere.");

        /// <summary>What a reader with no bundle is told, instead of a control that cannot work.</summary>
        public static string NeedsScript => Pick(
            "برای پخش در این صفحه به جاوااسکریپت نیاز است. می‌توانید فایل را دانلود کنید.",
            "Playing here needs JavaScript. You can download the file instead.");

        public static string UnlockAndPlay => Pick("باز کن و پخش کن", "Unlock and play");

        public static string Unlocking => Pick("در حال باز کردن…", "Unlocking…");

        public static string WrongKey => Pick("این رمز درست نیست.", "That is not the right passphrase.");

        /// <summary>
        /// The browser cannot do this, which is not the same as the passphrase being wrong.
        ///
        /// <para>Playing a locked file needs a Service Worker: a media element asks for byte ranges
        /// and only a worker can answer them with decrypted bytes. Absent in a private window, on
        /// plain http, and in browsers with it disabled — and the honest answer is to say so and
        /// name the way round, rather than to point the element at ciphertext it would report as a
        /// corrupt file.</para>
        /// </summary>
        public static string NoWorker => Pick(
            "این مرورگر نمی‌تواند فایل قفل‌شده را همین‌جا پخش کند. از طریق لینک اشتراک بازش کنید یا دانلودش کنید.",
            "This browser cannot play a locked file here. Open it through a share link, or download it.");
    }
}
