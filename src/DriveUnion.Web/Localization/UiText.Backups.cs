namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// «پشتیبان فهرست» — the operator's screen for the catalogue's backup of itself.
    ///
    /// <para>Nothing here is ever rendered for a customer. The route is behind the operator policy
    /// and the nav entry behind the same claim, and every word of it is about the operator's pool:
    /// which of their Google accounts is holding a copy of the index, and what to do with it.</para>
    ///
    /// <para>The page is unusually wordy on purpose. It is read twice in its life — once by somebody
    /// setting the panel up, and once by somebody whose database has just gone — and the second
    /// reader has no time to work anything out. The restore steps are on the screen rather than in a
    /// document, because a document lives somewhere that might be gone too.</para>
    /// </summary>
    public static class Backups
    {
        // ── the page ────────────────────────────────────────────────────────────────────────────

        public static string Title => Pick("پشتیبان فهرست فایل‌ها", "Catalogue backup");

        /// <summary>The sidebar has 232 pixels, so the menu gets the short form.</summary>
        public static string NavTitle => Pick("پشتیبان فهرست", "Catalogue backup");

        public static string Subtitle => Pick(
            "یک تصویر از فهرست فایل‌ها، نوشته‌شده داخل خودِ اکانت‌های گوگل.",
            "A snapshot of the file catalogue, written into the Google accounts themselves.");

        // ── why it exists ───────────────────────────────────────────────────────────────────────

        public static string WhyHeading => Pick(
            "چرا این وجود دارد",
            "Why this exists");

        /// <summary>
        /// The whole justification in one paragraph, said plainly. An operator who does not
        /// understand this treats the page as housekeeping and ignores a failed run.
        /// </summary>
        public static string WhyBody => Pick(
            "فایل‌های مشتری‌ها داخل اکانت‌های گوگل شماست و همان‌جا امن است. چیزی که فقط در پایگاه‌داده "
            + "وجود دارد این است که هر فایل مال کیست، اسمش چه بوده و روی کدام اکانت نشسته. اگر آن "
            + "پایگاه‌داده از دست برود، همه‌ی بایت‌ها سر جایشان هستند و هیچ‌کدام قابل دسترسی نیستند. "
            + "این صفحه همان نگاشت را داخل خودِ اکانت‌ها می‌نویسد تا فضای ذخیره‌سازی، فهرست خودش را "
            + "هم با خود داشته باشد.",
            "Customer files live in your Google accounts and are safe there. What exists only in the "
            + "database is which file belongs to whom, what it was called and which account holds "
            + "it. Lose that database and every byte is still sitting on Google's servers and not "
            + "one of them is reachable. This writes that mapping into the accounts themselves, so "
            + "the storage carries its own index.");

        // ── what is in it, and what is not ──────────────────────────────────────────────────────

        public static string ContentsHeading => Pick("داخلش چیست", "What is in it");

        public static string ContentsBody => Pick(
            "فضاهای کاری و اسم کوتاهشان، اکانت‌های استخر، پوشه‌های هر فضای کاری، و یک سطر برای هر "
            + "فایل: شناسه، صاحب، اکانت، شناسه‌ی فایل در درایو، نام، نوع، حجم و تاریخ‌ها — از جمله "
            + "فایل‌های داخل سطل زباله، چون آن‌ها هم هنوز بایت‌های واقعی در استخر هستند. سرآیندِ "
            + "رمزنگاری فایل‌های رمزشده هم داخلش است: بدون آن، فایلِ رمزشده حتی برای صاحبش هم برای "
            + "همیشه باز نشدنی است، و خودِ سرآیند بدون رمزعبورِ مشتری هیچ چیزی را باز نمی‌کند.",
            "Workspaces and their slugs, the pool accounts, each workspace's folders, and one line "
            + "per file: its id, its owner, its account, its Drive file id, name, type, size and "
            + "dates — including files in the trash, because those are still real bytes in the pool. "
            + "The encryption header of every encrypted file is in it too: without the header an "
            + "encrypted file is unopenable for ever even by its owner, and the header on its own "
            + "opens nothing without the customer's passphrase.");

        public static string OmittedHeading => Pick("داخلش نیست", "What is deliberately not in it");

        /// <summary>
        /// Said on the screen and not only in the code, because the file sits in a Drive folder and
        /// the operator is the person who decides who can see that folder.
        /// </summary>
        public static string OmittedBody => Pick(
            "هیچ اعتبارنامه‌ای: نه توکن‌های گوگل، نه کلیدهای S3، نه هش کلیدهای API، نه رمزعبور "
            + "کاربران، نه کلیدهای Data Protection، و نه نشانی لینک‌های اشتراک — چون آن نشانی خودش "
            + "کلیدِ دانلود است. این فایل داخل یک اکانت گوگل می‌نشیند و با آن مثل چیزی رفتار می‌شود "
            + "که ممکن است لو برود.",
            "No credentials of any kind: no Google tokens, no S3 keys, no API token hashes, no "
            + "password hashes, no Data Protection keys, and no share-link slugs — a slug is itself "
            + "the key to a download. This file sits in a Google account and is treated as something "
            + "that could leak.");

        // ── the schedule ────────────────────────────────────────────────────────────────────────

        public static string ScheduleHeading => Pick("زمان‌بندی", "The schedule");

        /// <param name="everyDays">How often a snapshot is taken.</param>
        /// <param name="keep">How many runs are kept before the oldest are deleted from the pool.</param>
        /// <param name="copies">How many accounts get a copy of each one.</param>
        public static string ScheduleBody(int everyDays, int keep, int copies) => Pick(
            $"هر {Numerals.Plain(everyDays)} روز یک‌بار، روی {Numerals.Plain(copies)} اکانت سالم "
            + $"(هرکدام یک نسخه)، و {Numerals.Plain(keep)} نسخه‌ی آخر نگه داشته می‌شود. بیش از یک "
            + "نسخه برای این است که «پایگاه‌داده از دست رفت» و «آن اکانت از دست رفت» اغلب یک "
            + "بعدازظهرند.",
            $"{(everyDays == 1 ? "Every day" : $"Every {everyDays} days")}, onto "
            + $"{(copies == 1 ? "one healthy account" : $"{copies} healthy accounts")}, keeping the "
            + $"newest {keep} runs. More than one copy because «the database is gone» and «that "
            + "account is gone» are more often the same afternoon than not.");

        public static string WhereHeading => Pick("کجا نوشته می‌شود", "Where it is written");

        public static string WhereBody => Pick(
            "داخل هر اکانت، در پوشه‌ی DriveUnion/.catalogue — کنارِ پوشه‌ی فضاهای کاری و نه داخل "
            + "هیچ‌کدامشان. هیچ مشتری‌ای به آن نمی‌رسد و هیچ عملیاتی که روی درختِ یک مشتری راه می‌رود "
            + "به آن نمی‌رسد.",
            "In each account, under DriveUnion/.catalogue — beside the workspace folders and inside "
            + "none of them. No customer reaches it, and nothing that walks a customer's tree "
            + "reaches it either.");

        // ── taking one by hand ──────────────────────────────────────────────────────────────────

        public static string RunNow => Pick("همین حالا یکی بگیر", "Take one now");

        public static string RunNowHint => Pick(
            "یک نسخه‌ی جدید در صف می‌گذارد؛ ظرف یک دقیقه نوشته می‌شود. پیش از هر کار پرریسکی — "
            + "مهاجرت اکانت، پاک‌سازی، یا به‌روزرسانی — ارزشش را دارد.",
            "Queues a fresh snapshot; it is written within the minute. Worth doing before anything "
            + "risky — an account migration, a purge, an upgrade.");

        public static string Queued => Pick(
            "در صف قرار گرفت. تا یک دقیقه‌ی دیگر در همین فهرست ظاهر می‌شود.",
            "Queued. It will appear in the list below within the minute.");

        public static string AlreadyQueued => Pick(
            "همین حالا یکی در صف یا در حال نوشتن است؛ چیز تازه‌ای اضافه نشد.",
            "One is already queued or being written; nothing new was added.");

        // ── the list ────────────────────────────────────────────────────────────────────────────

        public static string RecentHeading => Pick("نسخه‌های اخیر", "Recent snapshots");

        public static string ColumnName => Pick("فایل", "File");

        public static string ColumnTaken => Pick("زمان", "Taken");

        public static string ColumnStatus => Pick("وضعیت", "Status");

        public static string ColumnContents => Pick("محتوا", "Contents");

        public static string ColumnSize => Pick("حجم", "Size");

        public static string ColumnCopies => Pick("نسخه‌ها", "Copies");

        // Statuses are properties and never a method taking the enum: the catalogue test renders
        // every entry and cannot supply one. The view model maps the enum and names the entry.

        public static string StatusPending => Pick("در صف", "Queued");

        public static string StatusRunning => Pick("در حال نوشتن", "Writing");

        public static string StatusCompleted => Pick("نوشته شد", "Written");

        public static string StatusFailed => Pick("ناموفق", "Failed");

        public static string ByHand => Pick("دستی", "By hand");

        public static string Scheduled => Pick("زمان‌بندی‌شده", "Scheduled");

        /// <param name="files">Rows in the snapshot, which is every file in the product.</param>
        /// <param name="tenants">How many workspaces they belong to.</param>
        public static string Contents(int files, int tenants) => Pick(
            $"{Numerals.Count(files)} فایل در {Numerals.Count(tenants)} فضای کاری",
            $"{(files == 1 ? "1 file" : $"{Numerals.Count(files)} files")} in "
            + $"{(tenants == 1 ? "1 workspace" : $"{Numerals.Count(tenants)} workspaces")}");

        /// <summary>Its own line because these are the files that are lost for ever if this is wrong.</summary>
        public static string Encrypted(int files) => Pick(
            $"شامل {Numerals.Count(files)} سرآیند رمزنگاری",
            files == 1
                ? "including 1 encryption header"
                : $"including {Numerals.Count(files)} encryption headers");

        /// <param name="label">The account's short handle — <c>A1</c>, <c>A2</c>.</param>
        public static string CopyOn(string label) => Pick(
            $"روی {Ltr(label)}",
            $"on {Ltr(label)}");

        /// <summary>The row outlives the file: a snapshot that was pruned is not one that never ran.</summary>
        public static string CopyRemoved(string label) => Pick(
            $"{Ltr(label)} — پاک شده",
            $"{Ltr(label)} — pruned");

        public static string NoCopies => Pick("هیچ نسخه‌ای در استخر نیست", "No copy in the pool");

        // ── nothing yet, and something wrong ────────────────────────────────────────────────────

        public static string EmptyStateHeading => Pick(
            "هنوز هیچ نسخه‌ای گرفته نشده است.",
            "No snapshot has been taken yet.");

        public static string EmptyStateBody => Pick(
            "اولین نسخه خودبه‌خود گرفته می‌شود؛ اگر منتظر نمی‌مانید، دکمه‌ی بالا همین حالا یکی "
            + "می‌گیرد. تا آن لحظه، نگاشتِ فایل‌ها فقط در پایگاه‌داده وجود دارد.",
            "The first one is taken on its own; if you would rather not wait, the button above takes "
            + "one now. Until then the mapping exists only in the database.");

        /// <summary>
        /// The sentence that makes the page worth having. A backup that stopped working in March is
        /// worse than none, because somebody is relying on it.
        /// </summary>
        public static string StaleWarning(int days) => Pick(
            $"تازه‌ترین نسخه‌ی سالم {Numerals.Plain(days)} روز پیش گرفته شده است. اگر امروز "
            + "پایگاه‌داده را از دست بدهید، فایل‌هایی که از آن زمان آپلود شده‌اند در هیچ فهرستی "
            + "نیستند.",
            $"The newest good snapshot is {(days == 1 ? "a day" : $"{days} days")} old. If the "
            + "database were lost today, every file uploaded since then would be in no index at "
            + "all.");

        public static string NeverWarning => Pick(
            "هیچ نسخه‌ی سالمی وجود ندارد. اگر امروز پایگاه‌داده را از دست بدهید، هیچ راهی برای "
            + "فهمیدن اینکه کدام فایل مال کیست باقی نمی‌ماند.",
            "There is no good snapshot at all. If the database were lost today, nothing would be "
            + "left that knows which file belongs to whom.");

        // ── how to actually use one ─────────────────────────────────────────────────────────────

        public static string RestoreHeading => Pick("بازیابی از یک نسخه", "Restoring from one");

        /// <summary>On the screen rather than in a document, because a document may be gone too.</summary>
        public static string RestoreIntro => Pick(
            "این مراحل به هیچ بخشی از این برنامه نیاز ندارند. فایل، خطوط JSON فشرده‌شده با gzip است "
            + "— یک شیء در هر خط، با یک کلید type — و هر زبانی می‌تواند بخواندش.",
            "These steps need no part of this application. The file is gzipped JSON Lines — one "
            + "object per line, each with a type — and any language can read it.");

        public static string RestoreStep1 => Pick(
            "۱ — وارد یکی از اکانت‌های فهرست‌شده در ستون «نسخه‌ها» شوید و پوشه‌ی "
            + "DriveUnion/.catalogue را باز کنید. تازه‌ترین فایل، آنی است که نامش بزرگ‌ترین تاریخ "
            + "را دارد.",
            "1 — Sign into one of the accounts named in the Copies column and open "
            + "DriveUnion/.catalogue. The newest file is the one whose name carries the latest date.");

        public static string RestoreStep2 => Pick(
            "۲ — آخرین خط را نگاه کنید: باید یک سطر با type برابر footer باشد. اگر نبود، فایل ناقص "
            + "است و باید سراغ نسخه‌ی قبلی بروید.",
            "2 — Check the last line: it must be a record whose type is footer. If it is not, the "
            + "file is truncated and the run before it is the one to use.");

        public static string RestoreStep3 => Pick(
            "۳ — یک پایگاه‌داده‌ی خالی با همین برنامه بالا بیاورید تا جدول‌ها ساخته شوند، و "
            + "اکانت‌های گوگل را دوباره وصل کنید. اعتبارنامه‌ها عمداً داخل این فایل نیستند.",
            "3 — Bring up an empty database with this application so the tables are created, and "
            + "reconnect the Google accounts. Their credentials are deliberately not in this file.");

        public static string RestoreStep4 => Pick(
            "۴ — سطرهای tenant و account و folder و file و encryption را به همان ترتیب داخل "
            + "جدول‌های Tenants و GoogleAccounts و Folders و StoredFiles و FileEncryptions درج کنید. "
            + "شناسه‌ها همان‌هایی هستند که بودند، پس هیچ‌چیز نیاز به نگاشت دوباره ندارد.",
            "4 — Insert the tenant, account, folder, file and encryption records, in that order, "
            + "into Tenants, GoogleAccounts, Folders, StoredFiles and FileEncryptions. The ids are "
            + "the ones they always were, so nothing has to be remapped.");

        public static string RestoreStep5 => Pick(
            "۵ — کاربران و اعضای فضاهای کاری داخل این فایل نیستند و باید دوباره دعوت شوند. فایل‌ها "
            + "شناسه‌ی صاحبشان را نگه داشته‌اند، پس هر کاربری که با همان شناسه ساخته شود دوباره "
            + "صاحب فایل‌های خودش می‌شود.",
            "5 — Users and workspace membership are not in this file and have to be invited again. "
            + "Files keep their owner id, so a user recreated with that id owns their files again.");

        /// <summary>
        /// The one thing about the restore that is not obvious and cannot be undone by trying again.
        /// </summary>
        public static string RestoreCaveat => Pick(
            "فایل‌هایی که بعد از گرفتن این نسخه آپلود شده‌اند در آن نیستند. بایت‌هایشان هنوز در "
            + "استخر است اما هیچ سطری به آن‌ها اشاره نمی‌کند؛ پیش از پاک کردن هر چیزی از اکانت‌ها، "
            + "پوشه‌ها را با فهرست بازیابی‌شده مقایسه کنید.",
            "Files uploaded after this snapshot was taken are not in it. Their bytes are still in "
            + "the pool but nothing points at them; compare the folders against the restored "
            + "catalogue before deleting anything from the accounts.");
    }
}
