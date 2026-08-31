namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The one page the service worker keeps on the device.
    ///
    /// <para>Its own file rather than lines in <c>UiText.cs</c>, for the reason that class is
    /// partial: the main table had become the one place several unrelated pieces of work all had to
    /// edit at once.</para>
    ///
    /// <para>These words are read in a situation no other screen is written for. There is no
    /// network, so nothing on the page can be fetched and no link on it can be relied on to arrive
    /// anywhere; and the reader has just pressed something that did not work, so the first thing
    /// they need is to be told that the panel is not broken. Short sentences, no jargon about
    /// caches or workers, and nothing that promises what the next tap will do.</para>
    /// </summary>
    public static class Offline
    {
        /// <summary>The document title, and the heading — one word for one fact.</summary>
        public static string Title => Pick("آفلاین", "Offline");

        /// <summary>
        /// What happened, said as a fact about the connection rather than about the app.
        ///
        /// <para>"The page did not load" and not "something went wrong": somebody whose phone has
        /// lost signal already knows what to do about it, and the only thing this page can add is
        /// certainty about which half failed.</para>
        /// </summary>
        public static string Body => Pick(
            "این دستگاه به اینترنت وصل نیست، برای همین صفحه‌ای که خواستید باز نشد.",
            "This device has no connection, so the page you asked for did not load.");

        /// <summary>
        /// Why there is no file list on this screen either — which is the product's own claim,
        /// restated at the one moment a customer would otherwise read it as a fault.
        ///
        /// <para>An installed app that shows nothing offline looks unfinished unless somebody says
        /// why. The reason is the reason the product exists: names and files are not written to this
        /// phone, so there is nothing here to show without asking the server.</para>
        /// </summary>
        public static string NothingStored => Pick(
            "فایل‌ها و نام‌هایشان روی این دستگاه ذخیره نمی‌شوند، پس دیدنشان به اینترنت نیاز دارد.",
            "Files and their names are not kept on this device, so seeing them needs a connection.");

        /// <summary>
        /// The one control, and it is a link rather than a button on purpose.
        ///
        /// <para>A button that reloads needs JavaScript to mean anything, and the panel's rule is
        /// that nothing in a view may be written so that it only makes sense while a script is
        /// running. A link to the panel's front door works with the bundle or without it: when the
        /// connection is back it opens the panel, and when it is not it lands here again, which is
        /// the truthful answer rather than a spinner.</para>
        /// </summary>
        public static string Retry => Pick("تلاش دوباره", "Try again");

        // ── keeping a film on the device ──────────────────────────────────────────────────────────

        /// <summary>
        /// The control that keeps a copy.
        ///
        /// <para>«On this device» and not «offline»: the reader is choosing where a few gigabytes go,
        /// and the useful word is the one that says whose disk fills up.</para>
        /// </summary>
        public static string Keep => Pick("ذخیره روی این دستگاه", "Save on this device");

        public static string Keeping => Pick("در حال ذخیره…", "Saving…");

        public static string Kept => Pick("روی این دستگاه ذخیره شده", "Saved on this device");

        public static string Forget => Pick("حذف نسخهٔ ذخیره‌شده", "Remove the saved copy");

        /// <summary>
        /// Stopping a save that is running.
        ///
        /// <para>A figure with no way out is just a figure. Ten minutes into a six-gigabyte film the
        /// reader may well have changed their mind, and the alternative to this button is closing
        /// the tab — which works, but leaves them wondering what it left behind.</para>
        /// </summary>
        public static string StopKeeping => Pick("توقف", "Stop");

        /// <summary>
        /// What the browser asks before a page with a save running is closed.
        ///
        /// <para>Most browsers show their own wording and ignore this one; it is here because the
        /// ones that do not are the ones where it matters. What it protects is no longer the whole
        /// download — a save resumes from its last checkpoint now — but the run since that
        /// checkpoint, which is up to thirty-two megabytes of somebody's connection.</para>
        /// </summary>
        public static string LeavingStopsIt => Pick(
            "ذخیره‌سازی تمام نشده. با بستن این صفحه متوقف می‌شود و بعداً از همین‌جا ادامه می‌دهید.",
            "Saving is not finished. Closing this page stops it; you can carry on from here later.");

        /// <summary>
        /// Said when a save was stopped. What it stopped with is kept, so the sentence says so.
        ///
        /// <para>It used to end «and nothing was left on the device», which was true when a stop
        /// deleted what it had. It does not any more: somebody who stops a download at 80% to get on
        /// a train has not asked for those four gigabytes to be thrown away.</para>
        /// </summary>
        public static string KeepStopped => Pick(
            "متوقف شد. آنچه گرفته شده روی دستگاه مانده و می‌توانید ادامه بدهید.",
            "Stopped. What has been fetched is on the device, and you can carry on from there.");

        /// <summary>Carrying on a save that stopped. The same button, saying what it will do now.</summary>
        public static string Continue => Pick("ادامه", "Continue");

        /// <summary>How an unfinished one is labelled in the list, beside how far it got.</summary>
        public static string Unfinished => Pick("ناتمام", "Unfinished");

        /// <summary>
        /// Why a saved copy plays without asking, said <b>before</b> it is saved rather than after.
        ///
        /// <para>This is the whole of what the owner traded away by choosing to keep the decrypted
        /// copy rather than the encrypted one, and it is not a detail: a film kept for a flight is
        /// readable by anything on that device that can reach the browser's storage, and the
        /// passphrase stops being what stands between the two. Somebody about to press the button is
        /// the only person who can weigh that, so it is beside the button.</para>
        /// </summary>
        public static string KeptOpensWithoutTheKey => Pick(
            "نسخهٔ ذخیره‌شده رمزگشایی‌شده است: تا وقتی روی این دستگاه باشد بدون رمز باز می‌شود.",
            "The saved copy is decrypted: while it is on this device it opens without the "
            + "passphrase.");

        /// <summary>
        /// Refused for room, with both figures.
        ///
        /// <para>Two numbers rather than «not enough space», because the reader's next move depends
        /// entirely on the gap: 200 MB short is something to go and clear, and 5 GB short on a phone
        /// is not.</para>
        /// </summary>
        public static string NoRoom => Pick(
            "روی این دستگاه جا نیست.",
            "There is not enough room on this device.");

        /// <summary>Said where the two figures go. See <see cref="NoRoom"/>.</summary>
        public static string NeedsAndHas => Pick("لازم دارد", "needs");

        public static string FreeHere => Pick("جای آزاد", "free");

        /// <summary>A browser with no storage of this kind — an old one, or a private window.</summary>
        public static string CannotKeep => Pick(
            "این مرورگر نمی‌تواند فایلی روی دستگاه نگه دارد.",
            "This browser cannot keep a file on the device.");

        public static string KeepFailed => Pick(
            "ذخیره کامل نشد و چیزی روی دستگاه نماند.",
            "Saving did not finish, and nothing was left on the device.");

        // ── the screen that lists them ────────────────────────────────────────────────────────────

        public static string LibraryTitle => Pick("ذخیره‌شده روی این دستگاه", "Saved on this device");

        /// <summary>
        /// What this screen is, and the one thing about it somebody will not guess.
        ///
        /// <para>That it is per-device and per-browser. A list that looked like part of the account
        /// would have people wondering why their phone and their laptop disagree.</para>
        /// </summary>
        public static string LibraryHint => Pick(
            "این‌ها روی همین مرورگر و همین دستگاه نگه داشته شده‌اند و بدون اینترنت باز می‌شوند. "
            + "روی دستگاه دیگرتان دیده نمی‌شوند.",
            "These are kept in this browser on this device and open with no connection. They do not "
            + "appear on your other devices.");

        public static string LibraryEmpty => Pick(
            "هنوز چیزی روی این دستگاه ذخیره نکرده‌اید.",
            "Nothing has been saved on this device yet.");

        public static string ClearAll => Pick("خالی کردن همه", "Remove everything");

        /// <summary>How much of the device these are taking, which is the reason to open this screen.</summary>
        public static string UsingHere => Pick("روی این دستگاه", "on this device");

        public static string Watch => Pick("تماشا", "Watch");
    }
}
