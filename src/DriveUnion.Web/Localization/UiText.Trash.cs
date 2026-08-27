namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// «سطل زباله» — the customer's trash, and the one sentence this whole slice exists to say.
    ///
    /// <para>A customer who deletes a file, watches their usage figure stay where it was and is told
    /// nothing concludes the product is broken. That report is what started this phase, so the words
    /// that answer it are not a footnote on the screen: deleting moves a file here and frees nothing,
    /// and emptying is the button that gives the space back.</para>
    ///
    /// <para>Digits follow the product's rule. A byte quantity carries a unit and is a left-to-right
    /// technical readout, so it stays Latin in both languages and is written by
    /// <c>DisplayFormats.Bytes</c>; a count or a number of days set in prose takes that prose's own
    /// numerals through <see cref="Numerals"/>.</para>
    /// </summary>
    public static class Trash
    {
        public static string Title => Pick("سطل زباله", "Trash");

        public static string FileCount(int files) => Pick(
            $"{Numerals.Count(files)} فایل",
            files == 1 ? "1 file" : $"{Numerals.Count(files)} files");

        // ── the sentence the phase exists for ────────────────────────────────────────────────────

        public static string HowItWorksHeading => Pick(
            "حذف کردن، فضا آزاد نمی‌کند",
            "Deleting a file does not free space");

        /// <summary>
        /// Said before the list rather than under it. The customer's mental model is Drive's — the
        /// file is still somewhere and the space is still spent — and this is the sentence that puts
        /// it there before they go looking for a fault.
        /// </summary>
        public static string HowItWorksBody => Pick(
            "فایلی که حذف می‌کنید به اینجا می‌آید و همچنان از سهم فضای شما حساب می‌شود. فضا وقتی "
            + "برمی‌گردد که این فایل‌ها برای همیشه پاک شوند: یا با دکمه‌ی «خالی کردن سطل» در پایین "
            + "همین صفحه، که همان لحظه فضا را آزاد می‌کند، یا خودبه‌خود وقتی مهلت هر فایل تمام شود.",
            "A file you delete comes here and still counts against your storage. The space comes back "
            + "when these files are gone for good: either with the button at the foot of this page, "
            + "which frees it there and then, or on its own once a file's deadline passes.");

        // ── the table ───────────────────────────────────────────────────────────────────────────

        public static string ColumnName => Pick("نام", "Name");

        public static string ColumnSize => Pick("حجم", "Size");

        public static string ColumnDeleted => Pick("حذف شده", "Deleted");

        public static string ColumnPurge => Pick("پاک‌سازی", "Purged");

        public static string Restore => Pick("بازگردانی", "Restore");

        /// <summary>
        /// The accessible name of a per-row control, which without the file name would be one of a
        /// dozen identical buttons. The same rule the account cards follow.
        /// </summary>
        public static string RestoreNamed(string name) => Pick(
            $"بازگردانی فایل {name}",
            $"Restore {name}");

        /// <summary>Nothing is purged today; the deadline is the earliest the sweeper may take it.</summary>
        public static string PurgeInDays(int days) => Pick(
            $"{Numerals.Plain(days)} روز دیگر",
            days == 1 ? "In 1 day" : $"In {days} days");

        public static string PurgeDue => Pick("همین روزها", "Any time now");

        /// <summary>
        /// A file deleted before the trash existed. It has no deadline and the purge leaves it alone
        /// rather than inventing one — so the cell says that instead of drawing a blank the customer
        /// would read as "never".
        /// </summary>
        public static string PurgeNoDeadline => Pick("بدون مهلت", "No deadline");

        // ── the foot, where the space actually comes back ────────────────────────────────────────

        /// <param name="size">Already a byte quantity, which is Latin in either language.</param>
        public static string HoldingSize(string size) => Pick(
            $"{Ltr(size)} در سطل زباله",
            $"{Ltr(size)} in the trash");

        public static string Empty => Pick("خالی کردن سطل", "Empty the trash");

        /// <summary>
        /// On the button, because it is the one control here that cannot be undone. There is no
        /// confirmation dialog: the panel has to work with JavaScript off, and a page that asks again
        /// on the server would put a second screen between the customer and the space they came for.
        /// </summary>
        public static string EmptyHint => Pick(
            "همه‌ی فایل‌های این فهرست را همین حالا برای همیشه پاک می‌کند و فضایشان را آزاد می‌کند. "
            + "این کار برگشت‌پذیر نیست.",
            "Deletes every file in this list for good, right now, and gives the space back. This "
            + "cannot be undone.");

        // ── nothing in it ───────────────────────────────────────────────────────────────────────

        public static string EmptyStateHeading => Pick(
            "سطل زباله خالی است.",
            "The trash is empty.");

        public static string EmptyStateBody => Pick(
            "هیچ فضایی اینجا گرفتار نیست. فایلی که از این پس حذف کنید تا پایان مهلتش اینجا می‌ماند و "
            + "می‌توانید برش گردانید.",
            "No space is held here. A file you delete from now on waits here until its deadline, and "
            + "you can put it back.");

        public static string EmptyStateAction => Pick("رفتن به فایل‌ها", "Go to the files");

        // ── what the screen says back ───────────────────────────────────────────────────────────

        public static string Restored => Pick(
            "فایل به فهرست فایل‌ها برگشت.",
            "The file is back in your files.");

        /// <summary>
        /// One sentence for "not yours", "not in the trash" and "the purge got there first". The
        /// first of those must not be distinguishable from the other two — the difference between
        /// two answers is a way of asking whether another workspace holds a given file.
        /// </summary>
        public static string NotRestored => Pick(
            "این فایل در سطل زباله نبود. ممکن است پیش‌تر برگردانده شده باشد یا مهلتش تمام شده و پاک "
            + "شده باشد.",
            "That file is not in the trash. It may have been put back already, or its deadline may "
            + "have passed and the purge taken it.");

        public static string Emptied(int files) => Pick(
            $"{Numerals.Count(files)} فایل برای همیشه پاک شد و فضای آن آزاد شد.",
            files == 1
                ? "1 file is gone for good and its space is free."
                : $"{Numerals.Count(files)} files are gone for good and their space is free.");

        public static string EmptiedNothing => Pick(
            "سطل زباله خالی بود؛ چیزی پاک نشد.",
            "The trash was already empty; nothing was deleted.");
    }

    /// <summary>
    /// The capacity card above the customer's name in the sidebar.
    ///
    /// <para>Every figure on it is the customer's own. The operator's card in the same slot shows the
    /// daily 750 GB each Google account is allowed, which is a fact about the operator's pool and is
    /// exactly what M1 §1.4 says a customer must never see — not the number and not that a pool
    /// exists. The shape of the card is the operator's; the figures in it are the customer's.</para>
    /// </summary>
    public static class Capacity
    {
        public static string StorageLabel => Pick("فضای ذخیره‌سازی", "Storage");

        public static string TrafficLabel => Pick("ترافیک این ماه", "Traffic this month");

        /// <summary>
        /// What has been spent this month, against the allowance.
        ///
        /// <para>The first half used to be a dash, and the dash was the honest thing at the time:
        /// nothing counted what a workspace served, and a zero would have read as «you have used
        /// none» to a customer who had been serving downloads all month. It is a number now because
        /// there is a number — <c>ITrafficMeter</c> counts the bytes as the response body is copied,
        /// so it is what reached visitors rather than what was promised to them.</para>
        /// </summary>
        /// <param name="spent">Already a byte quantity, which is Latin in either language.</param>
        /// <param name="cap">The same.</param>
        [VerbatimText("a slash and two already-formatted byte quantities contain no words to translate")]
        public static string TrafficOfCap(string spent, string cap) => Pick($"{spent} / {cap}", $"{spent} / {cap}");

        /// <summary>
        /// What the figure is and what it is not.
        ///
        /// <para>It says so rather than leaving «ترافیک» to be guessed at, because a customer looking
        /// at a number next to a cap wants to know what would make it go up.</para>
        ///
        /// <para>It used to say «public links» and stop there, which matched a meter that only the
        /// public download path wrote to. Downloads through an API key and through the S3 gateway are
        /// counted now — they cost the operator the same bytes out of the same Google account — so
        /// the sentence names all three. Uploads still do not count, and neither does anything the
        /// panel renders for the owner.</para>
        ///
        /// <para>Must keep agreeing with <c>UiText.Plans.TrafficCounts</c>, which answers the same
        /// question on a different screen.</para>
        /// </summary>
        public static string TrafficCounts => Pick(
            "بایت‌هایی که این ماه از این فضای کاری بیرون رفته: لینک‌های عمومی، API و درگاه S3. "
            + "آپلود حساب نمی‌شود.",
            "Bytes served out of this workspace this month — public links, the API and the S3 "
            + "gateway. Uploads do not count.");

        public static string TrashLabel => Pick("در سطل زباله", "In the trash");

        /// <summary>
        /// Why a trash figure is on a capacity card at all: it is exactly the difference between what
        /// the customer believes they freed and what they actually did.
        /// </summary>
        public static string TrashTitle => Pick(
            "فضایی که هنوز آزاد نشده. با خالی کردن سطل زباله برمی‌گردد.",
            "Space you have not freed yet. Emptying the trash gives it back.");
    }

    /// <summary>
    /// The operator's own settings screen. Nothing here is ever rendered for a customer: the route is
    /// behind the operator policy and the nav entry behind the same claim.
    /// </summary>
    public static class OperatorSettings
    {
        public static string Title => Pick("تنظیمات پنل", "Panel settings");

        public static string Subtitle => Pick(
            "تنظیم‌هایی که به یک فضای کاری تعلق ندارند و روی همه‌ی آن‌ها اعمال می‌شوند.",
            "Settings that belong to no single workspace and apply to all of them.");

        public static string RetentionHeading => Pick(
            "مهلت نگهداری در سطل زباله",
            "How long the trash keeps a file");

        public static string RetentionField => Pick("مهلت", "Retention");

        public static string UnitDays => Pick("روز", "days");

        /// <summary>
        /// What this number does and — the half an operator will otherwise assume — what it does not.
        /// The window is read at the moment a file is deleted and written onto that file's own
        /// deadline, so lowering it shortens the wait for what is deleted next and cannot reach back.
        /// </summary>
        public static string RetentionAppliesForward => Pick(
            "این عدد در همان لحظه‌ی حذف روی مهلت خودِ آن فایل نوشته می‌شود و دیگر برای آن خوانده "
            + "نمی‌شود. پس تغییرش فقط روی فایل‌هایی اثر دارد که از این پس حذف می‌شوند: چیزی که دیروز "
            + "حذف شده مهلتی را که گرفته نگه می‌دارد — کوتاه کردن این عدد آن را زودتر پاک نمی‌کند و "
            + "بلند کردنش به آن وقت بیشتری نمی‌دهد.",
            "The number is written onto a file's own deadline at the moment it is deleted, and is "
            + "never read for that file again. So changing it reaches only what is deleted from now "
            + "on: what somebody deleted yesterday keeps the deadline it was given — shortening this "
            + "does not destroy it sooner, and lengthening it does not give it longer.");

        /// <summary>
        /// The other thing it does not touch, said because it is the answer to «then how does a
        /// customer get their space back today».
        /// </summary>
        public static string RetentionAndEmptying => Pick(
            "خالی کردن سطل زباله هم مستقل از این عدد است: مشتری هر وقت بخواهد سطل خودش را خالی "
            + "می‌کند و فضا همان لحظه آزاد می‌شود.",
            "Emptying the trash is independent of this number too: a customer empties their own "
            + "trash whenever they like, and the space is freed there and then.");

        public static string RetentionBounds(int minimum, int maximum) => Pick(
            $"بین {Numerals.Plain(minimum)} تا {Numerals.Plain(maximum)} روز.",
            $"Between {minimum} and {maximum} days.");

        /// <summary>Why the two ends are where they are, so the bounds are not read as arbitrary.</summary>
        public static string RetentionWhyBounds => Pick(
            "پایین‌تر از این، سطل زباله دیگر یک تور نجات نیست و فقط حذف را عقب می‌اندازد؛ بالاتر از "
            + "آن، فضای اکانت‌ها صرف فایل‌هایی می‌شود که کسی سراغشان نمی‌آید. پیش‌فرض همان عددی است "
            + "که گوگل درایو و دراپ‌باکس دارند، پس نیازی به توضیح ندارد.",
            "Below the floor the trash stops being a safety net and becomes a delay; above the "
            + "ceiling the accounts are spent on files nobody comes back for. The default is Drive's "
            + "own number, and Dropbox's, so it needs no explaining to anybody.");

        public static string Save => Pick("ذخیره", "Save");

        public static string Saved(int days) => Pick(
            $"مهلت نگهداری روی {Numerals.Plain(days)} روز تنظیم شد. از این پس هر فایلی که حذف شود "
            + "همین مهلت را می‌گیرد.",
            days == 1
                ? "The trash now keeps a file for 1 day. Everything deleted from now on gets that deadline."
                : $"The trash now keeps a file for {days} days. Everything deleted from now on gets "
                  + "that deadline.");

        public static string RefusedOutOfRange(int minimum, int maximum) => Pick(
            $"مهلت نگهداری باید عددی بین {Numerals.Plain(minimum)} تا {Numerals.Plain(maximum)} روز "
            + "باشد. چیزی ذخیره نشد.",
            $"The retention window has to be a whole number of days between {minimum} and {maximum}. "
            + "Nothing was saved.");

        /// <param name="when">Already in this language's own numerals — see <c>DisplayFormats.PanelDateTime</c>.</param>
        public static string LastChanged(string when) => Pick(
            $"آخرین تغییر: {when}",
            $"Last changed: {when}");

        public static string NeverChanged => Pick(
            "از راه‌اندازی تا حالا تغییر نکرده است.",
            "Unchanged since this panel was set up.");
    }
}
