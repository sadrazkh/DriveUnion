namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The notifications screen, and the sentences that end up on a lock screen.
    ///
    /// <para><b>Two audiences with different constraints in one section.</b> The screen's words are
    /// read by somebody who chose to open it and can be as long as they need to be. The lock-screen
    /// words are read by somebody who did not choose anything, in one line, next to an icon, with
    /// the panel's name already printed above them by the operating system — so they carry no
    /// greeting, no brand and no punctuation they can do without.</para>
    ///
    /// <para><b>None of them names anything of the customer's.</b> Not a file, not a workspace, not
    /// a link. A notification is drawn on a lock screen and kept in a notification centre for days:
    /// this product's claim is that the server holds no readable copy of a customer's files, and a
    /// phone accumulating their file names is that claim with an exception in it. The count in
    /// <see cref="DeletionFinishedBody"/> is the one number that travels, and a number of files is
    /// not a file. See <c>PushPayload</c>.</para>
    /// </summary>
    public static class Notifications
    {
        /// <summary>The screen's heading, and the sidebar's word for it. One entry, so they cannot drift.</summary>
        public static string Title => Pick("اعلان‌ها", "Notifications");

        public static string Subtitle => Pick(
            "خبر دادن روی همین دستگاه، وقتی پنل باز نیست.",
            "Being told on this device, when the panel is not open.");

        // ───────────────────────────────────────────────────────────── the card that explains itself

        public static string WhatIsSentHeading => Pick("چه چیزی فرستاده می‌شود", "What gets sent");

        /// <summary>
        /// The promise the whole payload design is about, said before permission is asked and not
        /// after. A reader who is told this afterwards has already decided.
        /// </summary>
        public static string WhatIsSentNoNames => Pick(
            "هیچ نام فایلی، نام فضای کاری یا لینکی در اعلان نیست — فقط این‌که چه اتفاقی افتاده و کجا "
            + "را باز کنید. متن اعلان روی گوشی می‌ماند و ما چیزی در آن نمی‌گذاریم که ماندنش اشکال داشته باشد.",
            "No file name, workspace name or link is ever in a notification — only what happened and "
            + "where to open it. A notification stays on the phone, so nothing is put in one that "
            + "would matter if it stayed.");

        public static string WhatIsSentEncrypted => Pick(
            "متن اعلان پیش از ارسال رمز می‌شود و فقط همین دستگاه می‌تواند بازش کند؛ سرویس اعلان مرورگر "
            + "که در میانه است، آن را نمی‌خواند.",
            "The text is encrypted before it is sent and only this device can open it; the browser's "
            + "push service in the middle cannot read it.");

        public static string WhatIsSentPerDevice => Pick(
            "اجازه‌ی اعلان برای همین دستگاه است. روی گوشی و لپ‌تاپ جداگانه باید روشن شود.",
            "Permission is per device. A phone and a laptop are turned on separately.");

        // ───────────────────────────────────────────────────────────── what is notified, and what is not

        public static string WhenHeading => Pick("چه وقت خبر می‌دهیم", "When you are told");

        public static string WhenRemoteFetch => Pick(
            "آپلود از روی لینک که تمام شود یا شکست بخورد.",
            "A link-upload finishes or fails.");

        public static string WhenDeletion => Pick(
            "حذف گروهی که در صف بوده، وقتی تمام شود.",
            "A queued deletion finishes.");

        public static string WhenAbuse => Pick(
            "گزارش تخلف تازه — فقط برای اپراتور.",
            "A new abuse report — for the operator only.");

        /// <summary>
        /// The omission, written down. A reader who is not told why their uploads are silent assumes
        /// the feature is broken and turns it off.
        /// </summary>
        public static string WhenNotUploads => Pick(
            "برای آپلود معمولی خبری فرستاده نمی‌شود: نوار پیشرفت آن جلوی چشمتان است و گوشی دست خودتان.",
            "An ordinary upload sends nothing: its progress is already on screen and the phone is in "
            + "your hand.");

        // ───────────────────────────────────────────────────────────── the control

        public static string EnableButton => Pick("روشن کردن اعلان‌ها", "Turn notifications on");

        public static string DisableButton => Pick("خاموش کردن روی این دستگاه", "Turn off on this device");

        /// <summary>
        /// The four sentences below are written by the script rather than by Razor, because they are
        /// the outcome of a press and the server never sees one. They are entries here all the same
        /// — a bundle is compiled once and cannot ask which language a request was in, so the view
        /// renders them into the mount point's <c>data-*</c> and the script reads them back. A
        /// literal in a <c>.ts</c> file is a string the English panel or the Persian one cannot say.
        /// </summary>
        public static string StatusOn => Pick("روی این دستگاه روشن است.", "On for this device.");

        public static string StatusOff => Pick("روی این دستگاه خاموش است.", "Off for this device.");

        public static string DevicesHeading => Pick("دستگاه‌های شما", "Your devices");

        /// <summary>
        /// How many devices this person has. A count and never a list: a device name is a fact about
        /// where somebody was, and the row carries none for that reason.
        /// </summary>
        public static string DeviceCount(string count) => Pick(
            $"{Ltr(count)} دستگاه اعلان می‌گیرد.",
            $"{Ltr(count)} device(s) are receiving notifications.");

        public static string NoDevices => Pick(
            "هیچ دستگاهی هنوز اعلان نمی‌گیرد.",
            "No device is receiving notifications yet.");

        // ───────────────────────────────────────────────────────────── the states a control cannot be offered in

        /// <summary>
        /// iOS asks for notification permission only from a web app that has been added to the home
        /// screen, and a button that can only fail is worse than no button.
        /// </summary>
        public static string NeedsHomeScreenHeading => Pick(
            "اول به صفحه‌ی خانه اضافه کنید",
            "Add it to your home screen first");

        public static string NeedsHomeScreenBody => Pick(
            "روی آیفون، اجازه‌ی اعلان فقط وقتی گرفته می‌شود که این پنل از «اشتراک‌گذاری ← افزودن به صفحه‌ی "
            + "خانه» نصب شده باشد. بعد از نصب، همین صفحه را از آیکون خانه باز کنید و دکمه این‌جا خواهد بود.",
            "On iPhone, notification permission can only be asked for once this panel has been "
            + "installed with Share → Add to Home Screen. Open this page from the home-screen icon "
            + "afterwards and the button will be here.");

        public static string UnsupportedHeading => Pick(
            "این مرورگر اعلان نمی‌دهد",
            "This browser cannot show notifications");

        public static string UnsupportedBody => Pick(
            "چیزی خراب نیست — این مرورگر امکان اعلان وب را ندارد. بقیه‌ی پنل مثل همیشه کار می‌کند.",
            "Nothing is broken — this browser has no web notifications. The rest of the panel works "
            + "as it always did.");

        public static string BlockedHeading => Pick("اعلان‌ها رد شده‌اند", "Notifications are blocked");

        /// <summary>
        /// A refused permission cannot be asked for again from the page — only the reader can undo
        /// it, in the browser's own settings — so the screen says where rather than offering a
        /// button that silently does nothing.
        /// </summary>
        public static string BlockedBody => Pick(
            "اجازه‌ی اعلان برای این سایت رد شده است. فقط از تنظیمات خود مرورگر می‌شود دوباره اجازه داد؛ "
            + "دکمه‌ی این صفحه دیگر کاری نمی‌کند.",
            "Permission for this site was refused. Only the browser's own settings can grant it "
            + "again; a button on this page can no longer do anything.");

        /// <summary>The operator has set no VAPID keys, so a subscription minted here could never be sent to.</summary>
        public static string NotConfiguredHeading => Pick(
            "هنوز راه‌اندازی نشده",
            "Not set up yet");

        public static string NotConfiguredBody => Pick(
            "اپراتور هنوز کلیدهای اعلان این نصب را تنظیم نکرده است. تا آن وقت روشن کردنش فقط اجازه‌ای "
            + "می‌گیرد که هیچ اعلانی از آن نمی‌آید.",
            "The operator has not set this installation's notification keys yet. Until then, turning "
            + "it on would take a permission that no notification could ever arrive through.");

        /// <summary>The operator's own copy of the same state: the two settings, spelled as they are set.</summary>
        [VerbatimText("configuration keys are the deployment's own spelling and a translated key sets nothing")]
        public static string ConfigurationKeys => "Push:PublicKey · Push:PrivateKey · Push:Subject";

        public static string ConfigurationProblem(string problem) => Pick(
            $"تنظیمات اعلان قابل استفاده نیست: {problem}",
            $"The notification settings are not usable: {problem}");

        // ───────────────────────────────────────────────────────────── what the page says after a press

        public static string RefusedByReader => Pick(
            "اجازه داده نشد، پس چیزی ثبت نشد.",
            "Permission was not given, so nothing was registered.");

        public static string Failed => Pick(
            "ثبت این دستگاه انجام نشد. بعداً دوباره امتحان کنید.",
            "Registering this device did not work. Try again later.");

        public static string TooManyDevices => Pick(
            "برای این حساب دستگاه‌های زیادی ثبت شده است. یکی را از روی خودش خاموش کنید.",
            "This account has too many registered devices. Turn one off from the device itself.");

        // ───────────────────────────────────────────────────────────── the lock screen

        public static string FetchFinishedTitle => Pick("آپلود از لینک تمام شد", "Link-upload finished");

        public static string FetchFinishedBody => Pick(
            "فایلی که خواسته بودید گرفته شود، رسید.",
            "The file you asked us to fetch has arrived.");

        public static string FetchFailedTitle => Pick("آپلود از لینک نشد", "Link-upload failed");

        public static string FetchFailedBody => Pick(
            "گرفتن آن فایل انجام نشد. صفحه‌ی انتقال‌ها می‌گوید چرا.",
            "Fetching that file did not work. The transfers screen says why.");

        public static string DeletionFinishedTitle => Pick("حذف تمام شد", "Deletion finished");

        /// <summary>
        /// The one number that reaches a lock screen. «How many files» says how big the job was and
        /// names none of them, which is the whole distinction this feature is built on.
        /// </summary>
        public static string DeletionFinishedBody(string count) => Pick(
            $"{Ltr(count)} فایل به زباله‌دان رفت.",
            $"{Ltr(count)} file(s) went to the trash.");

        public static string AbuseReportTitle => Pick("گزارش تخلف تازه", "New abuse report");

        /// <summary>
        /// It says why it is urgent, because the operator reading it at three in the morning is
        /// deciding whether to get up.
        /// </summary>
        public static string AbuseReportBody => Pick(
            "یک لینک عمومی گزارش شده. اگر گوگل زودتر ببیندش، اکانت مخزن معلق می‌شود.",
            "A public link was reported. If Google sees it first, the pool account is suspended.");
    }
}
