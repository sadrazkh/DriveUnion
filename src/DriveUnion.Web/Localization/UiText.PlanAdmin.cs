namespace DriveUnion.Web.Localization;

// The catalogue's own words, in their own file. UiText.cs had become the single place three
// unrelated pieces of work all had to edit at once, and the class was made partial for exactly
// this. The rule it exists for is unchanged: a key is a member, so a typo is a build error.
public static partial class UiText
{
    /// <summary>
    /// Editing the operator's tier catalogue: create, edit, reorder, retire, delete, and the one
    /// action that reaches workspaces.
    ///
    /// <para>Separate from <see cref="Plans"/>, which is read by a customer's own card as well as by
    /// the operator. Nothing in here is ever rendered to a customer — these are the words of the
    /// screen where the product's pricing is written, and half of them are about what an edit does
    /// <i>not</i> do.</para>
    ///
    /// <para>The digit rule is the product's: a quantity carrying a unit is an LTR technical readout
    /// and stays Latin in both languages, a count in prose takes that prose's numerals. So
    /// <c>6144 GB</c> in the form and «۳ فضای کاری» in the sentence beside it.</para>
    /// </summary>
    public static class PlanAdmin
    {
        // ── The catalogue table ──────────────────────────────────────────────────────────────────

        public static string NewTier => Pick("پلن تازه", "New tier");

        /// <summary>How many workspaces carry this tier. Deliberately not "how many an edit moves".</summary>
        public static string ColumnWorkspaces => Pick("فضاها", "On it");

        public static string ColumnActions => Pick("اقدام", "Actions");

        public static string Edit => Pick("ویرایش", "Edit");

        public static string Retire => Pick("بازنشسته کردن", "Retire");

        public static string Restore => Pick("برگرداندن", "Bring back");

        public static string Delete => Pick("حذف", "Delete");

        public static string MoveUp => Pick("یک پله بالا", "Move up");

        public static string MoveDown => Pick("یک پله پایین", "Move down");

        public static string DefaultBadge => Pick("پیش‌فرض", "Default");

        public static string DefaultBadgeTitle => Pick(
            "هر فضای کاری تازه روی همین پلن ساخته می‌شود، پس بازنشسته کردن، حذف و تغییر کدش رد می‌شود.",
            "Every new workspace is created on this tier, so retiring, deleting and re-coding it are refused.");

        /// <summary>
        /// The rule that makes «حذف» the wrong verb here, said once on the screen where somebody
        /// would look for it — not in an error after they pressed it.
        /// </summary>
        public static string RetireRatherThanDelete => Pick(
            "پلن معمولاً حذف نمی‌شود، بازنشسته می‌شود: تا وقتی حتی یک فضای کاری روی آن باشد، پایگاه "
            + "داده حذف را رد می‌کند. بازنشسته یعنی به کسی تازه فروخته نمی‌شود و هر کسی که روی آن "
            + "است، بدون هیچ تغییری کار می‌کند. حذف فقط برای پلنی است که هنوز هیچ‌کس رویش نیست.",
            "A tier is normally retired rather than deleted: while even one workspace is on it, the "
            + "database refuses the delete. Retired means it is sold to nobody new and everybody "
            + "already on it keeps working unchanged. Delete is only for a tier nobody is on yet.");

        /// <summary>
        /// The state <c>ValidateOnStart</c> deliberately cannot see, because checking it needs a
        /// database and a panel that will not boot while the database is briefly away is worse.
        /// So the screen carries it instead.
        /// </summary>
        public static string DefaultMissingHeading => Pick(
            "پلن پیش‌فرض وجود ندارد",
            "The default tier does not exist");

        public static string DefaultMissingBody(string code) => Pick(
            $"Plans:DefaultPlanCode روی «{code}» تنظیم شده و هیچ ردیفی این کد را ندارد. ثبت‌نام "
            + "بعدی با خطای ۵۰۰ رد می‌شود. یا پلنی با همین کد بسازید، یا تنظیمات را روی یکی از "
            + "پلن‌های زیر ببرید.",
            $"Plans:DefaultPlanCode is set to «{code}» and no tier holds that code. The next sign-up "
            + "fails with a 500. Either create a tier with that code, or point the setting at one of "
            + "the tiers below.");

        // ── The form ─────────────────────────────────────────────────────────────────────────────

        public static string EditTitle(string tier) => Pick(
            $"ویرایش پلن «{tier}»",
            $"Editing the «{tier}» tier");

        public static string CodeField => Pick("کد", "Code");

        public static string CodeHint => Pick(
            "حرف کوچک انگلیسی، رقم و خط تیره. همین کد است که در Plans:DefaultPlanCode نوشته می‌شود "
            + "و در تاریخچه‌ی سهمیه‌ی هر مشتری می‌ماند؛ مشتری هیچ‌وقت آن را نمی‌بیند.",
            "Lower-case latin letters, digits and hyphens. This is what goes into "
            + "Plans:DefaultPlanCode and what a customer's quota history records; the customer never "
            + "sees it.");

        public static string NameField => Pick("نام", "Name");

        public static string NameHint => Pick(
            "چیزی که مشتری روی کارت پلنش می‌خواند. هر وقت بخواهید عوض می‌شود.",
            "What the customer reads on their plan card. Renameable whenever.");

        public static string SeatsHint => Pick("تعداد نفر.", "A number of people.");

        /// <summary>
        /// The unit suffix beside each of the three byte fields. One unit in, one unit out — a field
        /// that read <c>6 TB</c> and wrote <c>6</c> would divide the tier by 1024 on every save.
        /// </summary>
        [VerbatimText("a unit symbol, which is Latin in both languages like every other byte readout")]
        public static string UnitGigabytes => Pick("GB", "GB");

        public static string UnitNote => Pick(
            "GB اینجا یعنی ۱۰۲۴×۱۰۲۴×۱۰۲۴ بایت — همان تقسیمی که همه‌ی اندازه‌های پنل با آن نوشته "
            + "می‌شوند. فقط گیگابایت کامل: عددی که می‌نویسید، همان عددی است که دفعه‌ی بعد در همین "
            + "کادر می‌بینید.",
            "GB here means 1024×1024×1024 bytes — the divisor every size in the panel is already "
            + "written with. Whole gigabytes only: the number you type is the number you see in this "
            + "box next time.");

        public static string StoredExactly(string size) => Pick(
            $"ذخیره‌شده: {size}",
            $"Stored: {size}");

        /// <summary>
        /// Only reachable for a row that was not written through this form. Saying it beats rounding
        /// somebody's tier by a few hundred megabytes because they opened the screen.
        /// </summary>
        public static string NotWholeGigabytes => Pick(
            "مقدار ذخیره‌شده گیگابایت کامل نیست و این کادر آن را رو به پایین گرد کرده است. ذخیره‌ی "
            + "این فرم عدد را واقعاً تغییر می‌دهد.",
            "The stored figure is not a whole number of gigabytes and this box has rounded it down. "
            + "Saving this form really will change the number.");

        /// <summary>
        /// Per-file is the error bar on the traffic allowance. A warning rather than a refusal — the
        /// numbers are the owner's — but an unwarned tier is one a customer screenshots.
        /// </summary>
        public static string FileIsLargeAgainstTraffic => Pick(
            "سقف هر فایل نسبت به ترافیک ماهانه بزرگ است: چند بار دانلود همان یک فایل، سهمیه‌ی ماه "
            + "را تمام می‌کند و مشتری این را «شمارنده خراب است» می‌خواند.",
            "The per-file ceiling is large next to the monthly traffic: a few downloads of one file "
            + "finish the month's allowance, and a customer reads that as a broken meter.");

        public static string Save => Pick("ذخیره", "Save");

        public static string Cancel => Pick("انصراف", "Cancel");

        // ── What an edit does not do ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The single most likely misunderstanding this screen can cause, said where the operator is
        /// typing rather than after a customer notices. It is a heading, not a footnote, on purpose.
        /// </summary>
        public static string MovesNobodyHeading => Pick(
            "ذخیره‌ی این فرم هیچ مشتری‌ای را جابه‌جا نمی‌کند",
            "Saving this form moves no customer");

        public static string MovesNobodyBody(long workspaces) => Pick(
            $"اعداد هر فضای کاری روی ردیف خودش ذخیره شده و در لحظه‌ی اعمال پلن از اینجا کپی شده "
            + $"است؛ هیچ بررسی‌ای در محصول به این جدول وصل نمی‌شود. الان {Numerals.Count(workspaces)} "
            + "فضای کاری روی این پلن است و بعد از ذخیره هم دقیقاً همان سقف‌های قبلی را دارند. اگر "
            + "می‌خواهید اعداد تازه به آن‌ها برسد، باید پلن را دوباره روی‌شان اعمال کنید.",
            "Every workspace's numbers sit on its own row, copied from here at the moment the tier "
            + $"was applied; nothing in the product joins a check back to this table. "
            + $"{Numerals.Count(workspaces)} workspace(s) are on this tier and after you save they "
            + "will hold exactly the ceilings they hold now. Reaching them with the new numbers "
            + "means re-applying the tier.");

        public static string MovesNobodyOnAnEmptyTier => Pick(
            "هنوز هیچ فضای کاری روی این پلن نیست، پس این ویرایش فقط روی مشتری‌های بعدی اثر دارد.",
            "No workspace is on this tier yet, so this edit reaches only the customers who come next.");

        // ── Re-applying a tier ───────────────────────────────────────────────────────────────────

        public static string ReapplyAction => Pick(
            "اعمال دوباره روی مشتری‌های این پلن",
            "Re-apply to the workspaces on this tier");

        public static string ReapplyTitle(string tier) => Pick(
            $"اعمال دوباره‌ی پلن «{tier}»",
            $"Re-applying the «{tier}» tier");

        public static string ReapplyCounts(int workspaces, int moving) => Pick(
            $"{Numerals.Count(workspaces)} فضای کاری روی این پلن است و {Numerals.Count(moving)} تای "
            + "آن‌ها الان عددی متفاوت با پلن دارند. فقط همان‌ها جابه‌جا می‌شوند؛ بقیه از قبل روی "
            + "همین اعداد هستند.",
            $"{Numerals.Count(workspaces)} workspace(s) are on this tier and {Numerals.Count(moving)} "
            + "of them currently hold numbers that differ from it. Only those move; the rest are "
            + "already on these figures.");

        /// <summary>
        /// The reason this action is a separate screen with a confirm on it. A negotiated override
        /// is the normal shape of selling to a business, and taking one back by accident is the
        /// expensive mistake this button can make.
        /// </summary>
        public static string ReapplyTakesBackOverrides => Pick(
            "هر عدد توافق‌شده‌ای که جداگانه روی این فضاها گذاشته شده، با این کار به عدد پلن "
            + "برمی‌گردد. اگر به مشتری‌ای سقف جداگانه فروخته‌اید، اول تاریخچه‌ی همان فضای کاری را "
            + "ببینید.",
            "Any negotiated figure set separately on those workspaces goes back to the tier's. If "
            + "you sold a customer their own ceiling, look at that workspace's history first.");

        public static string ReapplyIsAudited => Pick(
            "برای هر عددی که جابه‌جا شود یک سطر در تاریخچه‌ی همان فضای کاری نوشته می‌شود، با همین "
            + "دلیلی که اینجا می‌نویسید. این همان چیزی است که به «چرا سهمیه‌ام عوض شد» جواب می‌دهد.",
            "Every number that moves gets a row in that workspace's own history, carrying the reason "
            + "you write here. That is what answers «why did my quota change».");

        public static string ReapplyConfirm => Pick("اعمال روی همه", "Re-apply to all");

        public static string ReapplyDone(int workspaces) => Pick(
            $"{Numerals.Count(workspaces)} فضای کاری روی اعداد تازه‌ی این پلن رفت.",
            $"{Numerals.Count(workspaces)} workspace(s) moved onto this tier's new numbers.");

        public static string ReapplyMovedNobody => Pick(
            "هیچ فضای کاری عددی متفاوت با پلن نداشت، پس چیزی جابه‌جا نشد و چیزی هم در تاریخچه نوشته نشد.",
            "No workspace held numbers that differ from the tier, so nothing moved and nothing was "
            + "written to any history.");

        public static string ReapplyOnAnEmptyTier => Pick(
            "هیچ فضای کاری روی این پلن نیست.",
            "No workspace is on this tier.");

        // ── What happened ────────────────────────────────────────────────────────────────────────

        public static string TierCreated(string tier) => Pick(
            $"پلن «{tier}» ساخته شد.",
            $"The «{tier}» tier was created.");

        public static string TierSaved(string tier) => Pick(
            $"پلن «{tier}» ذخیره شد. هیچ فضای کاری‌ای جابه‌جا نشد.",
            $"The «{tier}» tier was saved. No workspace moved.");

        public static string TierRetired(string tier) => Pick(
            $"پلن «{tier}» بازنشسته شد. هر کسی که رویش بود، بدون تغییر کار می‌کند.",
            $"The «{tier}» tier was retired. Everybody on it keeps working unchanged.");

        public static string TierRestored(string tier) => Pick(
            $"پلن «{tier}» دوباره فعال شد.",
            $"The «{tier}» tier is on sale again.");

        public static string TierDeleted(string tier) => Pick(
            $"پلن «{tier}» حذف شد.",
            $"The «{tier}» tier was deleted.");

        public static string TierMoved(string tier) => Pick(
            $"جای پلن «{tier}» در فهرست عوض شد.",
            $"The «{tier}» tier moved in the list.");

        // ── Refusals ─────────────────────────────────────────────────────────────────────────────

        public static string RefusedCodeMalformed => Pick(
            "کد پلن باید با حرف کوچک انگلیسی شروع شود و فقط حرف کوچک، رقم و خط تیره داشته باشد، "
            + "بین ۲ تا ۳۲ نویسه. این کد در فایل تنظیمات نوشته می‌شود، پس فاصله و حرف بزرگ ندارد.",
            "A plan code starts with a lower-case latin letter and holds only lower-case letters, "
            + "digits and hyphens, between 2 and 32 characters. It goes into a configuration file, "
            + "so it carries no spaces and no capitals.");

        public static string RefusedCodeTaken => Pick(
            "پلن دیگری همین کد را دارد. کد یکتاست، چون هم تنظیمات و هم تاریخچه‌ی سهمیه پلن را با "
            + "همین کد صدا می‌زنند.",
            "Another tier already holds this code. A code is unique because configuration and quota "
            + "history both name a tier by it.");

        public static string RefusedNameInvalid => Pick(
            "نام پلن را بنویسید؛ حداکثر ۱۲۰ نویسه.",
            "Write the tier's name; 120 characters at most.");

        public static string RefusedNumberOutOfRange => Pick(
            "هر سه سقف باید عددی درست بین ۱ تا ۱۰۴۸۵۷۶ گیگابایت باشند و سقف اعضا دست‌کم یک نفر. "
            + "سقف صفر یعنی هیچ آپلودی روی این پلن پذیرفته نمی‌شود.",
            "Each of the three ceilings is a whole number between 1 and 1048576 GB, and the seat "
            + "limit is at least one person. A ceiling of zero refuses every upload on the tier.");

        public static string RefusedFileLargerThanStorage => Pick(
            "سقف هر فایل از سقف کل فضا بیشتر است. چنین فایلی هیچ‌وقت ذخیره نمی‌شود، چون اول سقف "
            + "فضا آن را رد می‌کند — عددی است که پلن نمی‌تواند به آن عمل کند.",
            "The per-file ceiling is above the storage cap. Such a file could never be stored — the "
            + "storage check refuses it first — so it is a number the tier cannot honour.");

        public static string RefusedDefaultCannotBeRecoded(string code) => Pick(
            $"کد این پلن عوض نمی‌شود: Plans:DefaultPlanCode روی «{code}» تنظیم است و هر فضای کاری "
            + "تازه با همین کد ساخته می‌شود. اول تنظیمات را روی پلن دیگری ببرید.",
            $"This tier's code cannot change: Plans:DefaultPlanCode is «{code}» and every new "
            + "workspace is created from it. Point the setting at another tier first.");

        public static string RefusedDefaultCannotBeRetired(string code) => Pick(
            $"پلن پیش‌فرض بازنشسته نمی‌شود: Plans:DefaultPlanCode روی «{code}» است و ثبت‌نام بعدی "
            + "روی همین پلن انجام می‌شود. اول تنظیمات را روی پلن دیگری ببرید.",
            $"The default tier cannot be retired: Plans:DefaultPlanCode is «{code}» and the next "
            + "sign-up is created on it. Point the setting at another tier first.");

        public static string RefusedDefaultCannotBeDeleted(string code) => Pick(
            $"پلن پیش‌فرض حذف نمی‌شود: Plans:DefaultPlanCode روی «{code}» است و ثبت‌نام بعدی روی "
            + "همین پلن انجام می‌شود. اول تنظیمات را روی پلن دیگری ببرید.",
            $"The default tier cannot be deleted: Plans:DefaultPlanCode is «{code}» and the next "
            + "sign-up is created on it. Point the setting at another tier first.");

        public static string RefusedInUseCannotBeDeleted => Pick(
            "دست‌کم یک فضای کاری روی این پلن است، پس حذف نمی‌شود — پایگاه داده هم آن را رد می‌کند. "
            + "به‌جایش بازنشسته‌اش کنید: کسی که رویش هست بدون تغییر کار می‌کند و به کس تازه‌ای "
            + "فروخته نمی‌شود.",
            "At least one workspace is on this tier, so it is not deleted — the database refuses it "
            + "too. Retire it instead: everybody on it keeps working unchanged and nobody new is "
            + "sold it.");
    }
}
