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
        /// <summary>
        /// What unlocking does, and where the opened file goes.
        ///
        /// <para>It used to end «no readable copy is stored anywhere», which was true of the player
        /// and stopped being true of the product the moment a film could be kept for offline: what
        /// that keeps is the decrypted copy. The sentence now says what is true of watching, and
        /// says nothing about storing — the control that stores has its own warning beside it, which
        /// is where somebody deciding to press it will be looking.</para>
        /// </summary>
        public static string LockedHint => Pick(
            "رمزش را بزنید تا همین‌جا در مرورگر خودتان باز شود و پخش شود. رمز به سرور فرستاده نمی‌شود.",
            "Enter the passphrase and it is opened here, in your own browser, as it plays. The "
            + "passphrase is never sent to the server.");

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

        // ── carrying on from where it stopped ─────────────────────────────────────────────────────

        /// <summary>
        /// Said once the player has already moved, with the timecode beside it.
        ///
        /// <para>Past tense, because by the time this is legible the seek has happened — the film is
        /// at 42:15 and the reader is looking at a frame they do not recognise. A sentence offering
        /// to carry on would be describing a decision that was already taken, and the reader's
        /// question at that moment is not «shall I» but «why am I here». So it answers that, and
        /// puts the way back beside it.</para>
        ///
        /// <para>It seeks rather than asks because asking costs a tap on every visit to buy an
        /// answer that is the same one almost every time. The one case it is wrong for — somebody
        /// who wanted the beginning — costs one tap, and only them.</para>
        /// </summary>
        public static string ResumedFrom => Pick("از این‌جا ادامه داده شد:", "Carried on from");

        /// <summary>
        /// The way back to the beginning, which is the whole of the escape hatch.
        ///
        /// <para>A button and not a link: it changes what this page is doing rather than going
        /// anywhere, and it also forgets the position, so pressing it twice is not a way to end up
        /// somewhere unexpected.</para>
        /// </summary>
        public static string StartOver => Pick("از اول", "Start from the beginning");

        /// <summary>
        /// Where the remembered place is kept, said on the page that does the remembering.
        ///
        /// <para><b>This is a note about the reader's own device, and it is owed to them.</b> What is
        /// written is «this file, this far in» — which on the public watch page is written into the
        /// browser of a stranger who did nothing but open a link. It goes no further than that
        /// browser and is never sent to the server, and the only way somebody can know that is if it
        /// is said. It is said here, beside the evidence, rather than in a policy page nobody on a
        /// phone is going to open.</para>
        ///
        /// <para>Drawn only when there was a position to carry on from, so a first viewing is not
        /// interrupted by a paragraph about storage. That is also the one moment the sentence is
        /// answering a question the reader actually has — the film moved, and this says who
        /// remembered.</para>
        /// </summary>
        public static string PositionIsLocal => Pick(
            "این نقطه فقط در همین مرورگر و روی همین دستگاه نگه داشته می‌شود و به سرور فرستاده نمی‌شود.",
            "This place is kept in this browser on this device only, and is never sent to the "
            + "server.");
    }
}
