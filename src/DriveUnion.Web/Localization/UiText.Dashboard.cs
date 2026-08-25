namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// «داشبورد» — the screen the sidebar's first item has always claimed to open, in the words of
    /// whichever of the two audiences is reading it.
    ///
    /// <para><b>The section is one class and the screens are two.</b> Everything under
    /// <c>Customer…</c> is about a workspace's own figures; everything under <c>Pool…</c>,
    /// <c>Workspaces…</c> and <c>Transfers…</c> is about the operator's Google accounts and every
    /// customer at once. M1 §1.4 is what separates them: a customer must never learn which account
    /// holds their file, that a pool exists, or what it costs to run — so the two never share an
    /// entry, and a view that reached for the wrong prefix would be saying something recognisably
    /// out of place rather than quietly leaking a fact.</para>
    ///
    /// <para>Digits follow the product's rule. A byte quantity is an LTR technical readout and stays
    /// Latin in both languages — <c>DisplayFormats.Bytes</c> writes it and the view wraps the run in
    /// <c>dir="ltr"</c> — while a count set in prose takes that prose's own numerals through
    /// <see cref="Numerals"/>.</para>
    /// </summary>
    public static class Dashboard
    {
        // ── the customer's screen ───────────────────────────────────────────────────────────────

        public static string CustomerSubtitle => Pick(
            "یک نگاه به آنچه دارید و آنچه بر سرش می‌آید.",
            "One look at what you have and what is happening to it.");

        /// <param name="files">A count of things a person counts, so it takes the prose's numerals.</param>
        public static string FilesStored(int files) => Pick(
            $"{Numerals.Count(files)} فایل ذخیره‌شده",
            files == 1 ? "1 file stored" : $"{Numerals.Count(files)} files stored");

        public static string LiveLinksLabel => Pick("لینک‌های فعال", "Live links");

        /// <summary>
        /// «۳ از ۵» — how many of the workspace's links still work.
        ///
        /// <para>Both halves, because either on its own misleads: «۳ فعال» hides the two that have
        /// expired and «۵ لینک» promises five that work.</para>
        /// </summary>
        public static string LiveOfTotal(int live, int total) => Pick(
            $"{Numerals.Count(live)} از {Numerals.Count(total)}",
            $"{Numerals.Count(live)} of {Numerals.Count(total)}");

        public static string NoLinksYet => Pick(
            "هنوز لینکی نساخته‌اید.",
            "You have not made a link yet.");

        public static string DownloadsAllTimeLabel => Pick("دانلود", "Downloads");

        public static string DownloadsExplained => Pick(
            "هر دانلود وقتی شمرده می‌شود که کسی فایل را از یکی از لینک‌های شما بگیرد.",
            "A download is counted when somebody takes a file through one of your links.");

        /// <summary>
        /// What this figure is <b>not</b>, said because a reader will otherwise assume it is «this
        /// month».
        ///
        /// <para>It is every download since the link was made. There is no per-month or per-day
        /// figure beside it, and the honest reason is a cost: a window has to be counted from the
        /// download log, which is indexed by link and not by date, so asking «how many this month»
        /// on the panel's most-visited page would mean reading every download the workspace has ever
        /// served. The per-link counts below answer «what is being downloaded» until that figure can
        /// be produced without the read.</para>
        /// </summary>
        public static string DownloadsAreLifetime => Pick(
            "این عدد از ابتدای ساخت هر لینک شمرده شده است، نه در یک بازه‌ی مشخص. تفکیک ماهانه هنوز "
            + "وجود ندارد.",
            "That figure counts every download since each link was made, not a period. There is no "
            + "per-month breakdown yet.");

        /// <param name="files">A count in prose.</param>
        public static string TrashHolds(int files) => Pick(
            $"{Numerals.Count(files)} فایل حذف‌شده که فضایشان هنوز آزاد نشده است.",
            files == 1
                ? "1 deleted file whose space is not free yet."
                : $"{Numerals.Count(files)} deleted files whose space is not free yet.");

        public static string TrashIsEmpty => Pick(
            "چیزی در سطل زباله نیست؛ هیچ فضایی گرفتار نمانده است.",
            "Nothing is in the trash, so no space is held there.");

        public static string RecentUploadsHeading => Pick("آخرین آپلودها", "Latest uploads");

        public static string NoUploadsYet => Pick(
            "هنوز فایلی آپلود نکرده‌اید.",
            "You have not uploaded a file yet.");

        public static string NoUploadsAction => Pick("آپلود اولین فایل", "Upload your first file");

        /// <summary>
        /// The accessible name of a row that would otherwise be one of six identical links. The same
        /// rule the trash's restore buttons follow.
        /// </summary>
        public static string OpenFile(string name) => Pick(
            $"باز کردن {name}",
            $"Open {name}");

        public static string BusyLinksHeading => Pick("پربازدیدترین لینک‌ها", "Busiest links");

        public static string NoDownloadsYet => Pick(
            "هنوز هیچ‌کدام از لینک‌های شما دانلود نشده است.",
            "None of your links has been downloaded yet.");

        /// <summary>«۲۴۱ / ۵۰۰» — downloads against the cap the customer set on that link.</summary>
        public static string DownloadsOfCap(int taken, int cap) => Pick(
            $"{Numerals.Count(taken)} / {Numerals.Count(cap)}",
            $"{Numerals.Count(taken)} / {Numerals.Count(cap)}");

        /// <summary>The same figure for a link with no cap: a count on its own, and no invented ∞.</summary>
        public static string DownloadsUncapped(int taken) => Pick(
            $"{Numerals.Count(taken)} دانلود",
            taken == 1 ? "1 download" : $"{Numerals.Count(taken)} downloads");

        public static string AllFiles => Pick("همه‌ی فایل‌ها ←", "All files →");

        public static string AllLinks => Pick("همه‌ی لینک‌ها ←", "All links →");

        public static string OpenTrash => Pick("سطل زباله ←", "The trash →");

        // ── the operator's screen ───────────────────────────────────────────────────────────────

        public static string OperatorSubtitle => Pick(
            "دو پرسش، در یک نگاه: فضا دارد تمام می‌شود؟ چیزی خراب است؟",
            "Two questions at a glance: is storage running out, and is anything broken?");

        public static string PoolHeading => Pick("استخر ذخیره‌سازی", "The storage pool");

        /// <param name="accounts">A count of accounts, in prose.</param>
        public static string PoolAccounts(int accounts) => Pick(
            $"{Numerals.Count(accounts)} اکانت متصل",
            accounts == 1 ? "1 connected account" : $"{Numerals.Count(accounts)} connected accounts");

        public static string PoolUsedLabel => Pick("مصرف استخر", "Pool used");

        public static string NoAccountsHeading => Pick(
            "هیچ اکانت گوگلی متصل نیست.",
            "No Google account is connected.");

        public static string NoAccountsBody => Pick(
            "تا وقتی اکانتی وصل نشده باشد هیچ فایلی جایی برای رفتن ندارد و هر آپلودی رد می‌شود.",
            "Until one is connected a file has nowhere to go, and every upload is refused.");

        public static string ConnectAccounts => Pick("اکانت‌های گوگل ←", "Google accounts →");

        /// <param name="accounts">How many accounts are disconnected right now.</param>
        public static string AccountsDisconnected(int accounts) => Pick(
            $"{Numerals.Count(accounts)} اکانت قطع شده و آپلودی به آن نمی‌رود. لینک‌های دانلودی که "
            + "فایلشان آنجاست همچنان کار می‌کنند.",
            accounts == 1
                ? "1 account is disconnected and no upload is routed to it. Download links whose "
                  + "files live there still work."
                : $"{Numerals.Count(accounts)} accounts are disconnected and no upload is routed to "
                  + "them. Download links whose files live there still work.");

        /// <summary>
        /// The daily allowance, said in words because there is no number to say it with.
        ///
        /// <para>Google allows each account 750 GB of upload a day and this product counts none of
        /// it. A bar drawn from nothing would be a bar that is always empty, and an operator reading
        /// it on the day the pool actually stops accepting uploads would conclude the panel is
        /// broken — after concluding, all month, that they had room they did not have. So the card
        /// says what is not measured instead of drawing a figure that is not true.</para>
        /// </summary>
        public static string DailyUploadNotMetered => Pick(
            "مصرف آپلود امروز اندازه‌گیری نمی‌شود. سهمیه‌ی روزانه‌ی گوگل به ازای هر اکانت است و این "
            + "پنل هنوز چیزی از آن نمی‌شمارد، پس اینجا عددی نشان داده نمی‌شود.",
            "Today's upload is not measured. Google's daily allowance is per account and this panel "
            + "counts none of it yet, so no figure is shown here.");

        /// <summary>
        /// The other missing meter, said where the comp draws its egress chart. Same rule: the
        /// traffic a plan sells is carried on the tenant row and metered by nobody.
        /// </summary>
        public static string EgressNotMetered => Pick(
            "نمودار ترافیک خروجی وقتی می‌آید که ترافیک اندازه‌گیری شود. ترافیک ماهانه‌ای که در "
            + "پلن‌ها فروخته می‌شود روی ردیف هر فضای کاری هست، اما چیزی مصرفش را نمی‌شمارد.",
            "The egress chart arrives when traffic is metered. The monthly traffic the plans sell is "
            + "carried on each workspace's row, and nothing counts what is spent against it.");

        public static string WorkspacesHeading => Pick("نزدیک به سقف", "Near their ceiling");

        /// <param name="percent">The threshold a workspace joins this list at.</param>
        public static string WorkspacesNote(int percent) => Pick(
            $"فضاهای کاری‌ای که بیش از {Numerals.Plain(percent)}٪ سقف ذخیره‌سازی‌شان را پر کرده‌اند.",
            $"Workspaces that have filled more than {percent}% of their storage cap.");

        public static string NobodyNearCeiling => Pick(
            "هیچ فضای کاری نزدیک سقفش نیست.",
            "No workspace is near its ceiling.");

        public static string NoWorkspaces => Pick(
            "هنوز فضای کاری‌ای ساخته نشده است.",
            "No workspace has been created yet.");

        /// <param name="workspaces">A count of workspaces, in prose.</param>
        public static string WorkspaceCount(int workspaces) => Pick(
            $"{Numerals.Count(workspaces)} فضای کاری",
            workspaces == 1 ? "1 workspace" : $"{Numerals.Count(workspaces)} workspaces");

        public static string AllWorkspaces => Pick("همه‌ی فضاهای کاری ←", "All workspaces →");

        public static string TransfersHeading => Pick("انتقال‌ها", "Transfers");

        public static string TransfersInFlightLabel => Pick("در جریان", "In flight");

        /// <param name="hours">The window the failure count covers.</param>
        public static string TransfersFailedLabel(int hours) => Pick(
            $"ناموفق در {Numerals.Plain(hours)} ساعت اخیر",
            hours == 1 ? "Failed in the last hour" : $"Failed in the last {hours} hours");

        public static string FailuresHeading => Pick("کارهای ناموفق", "What failed");

        public static string NothingFailed => Pick(
            "در این بازه هیچ آپلودی شکست نخورده است.",
            "No upload failed in this window.");

        /// <summary>
        /// Said above the diagnostics: they are the words the failure arrived in, usually Google's,
        /// and they are here and on no other screen.
        /// </summary>
        public static string FailuresAreDiagnostics => Pick(
            "متن هر خطا همان چیزی است که رسیده و ترجمه نشده؛ این متن فقط روی صفحه‌های اپراتور "
            + "نمایش داده می‌شود.",
            "Each message is the words the failure arrived in, untranslated. It is shown on the "
            + "operator's screens and nowhere else.");

        public static string NoReasonRecorded => Pick(
            "دلیلی ثبت نشده است.",
            "No reason was recorded.");

        /// <param name="failures">How many failures the window holds in total.</param>
        public static string MoreFailures(int failures) => Pick(
            $"و {Numerals.Count(failures)} مورد دیگر در همین بازه.",
            failures == 1
                ? "And 1 more in the same window."
                : $"And {Numerals.Count(failures)} more in the same window.");
    }
}
