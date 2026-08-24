namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The operator's workspace and account screens — «فضاهای کاری» and one workspace's page.
    ///
    /// <para>These words are operator vocabulary and never reach a customer. The slug in particular:
    /// it is the folder name inside the operator's Google accounts, and M1 §1.4 makes it a hard rule
    /// that a customer must never learn which Google account holds their file. Nothing here is
    /// rendered on a tenant-facing screen, and the routes are behind the operator policy rather than
    /// behind a hidden link.</para>
    ///
    /// <para>Digits follow the product's rule, which is the same one <see cref="Plans"/> states: a
    /// quantity carrying a unit is a left-to-right technical readout and stays Latin in both
    /// languages, and a count set in prose takes that prose's numerals. So «۳ از ۵» for seats and
    /// <c>124 / 500 GB</c> for bytes. A slug is a readout too, and stays exactly as it was typed.</para>
    /// </summary>
    public static class Tenants
    {
        public static string Title => Pick("فضاهای کاری", "Workspaces");

        public static string Subtitle => Pick(
            "هر مشتری یک فضای کاری است. ساخت فضای کاری و حساب‌های داخل آن فقط از همین‌جا انجام می‌شود.",
            "One workspace per customer. Workspaces and the accounts inside them are made here and "
            + "nowhere else.");

        public static string Count(int workspaces) => Pick(
            $"{Numerals.Count(workspaces)} فضای کاری",
            workspaces == 1 ? "1 workspace" : $"{Numerals.Count(workspaces)} workspaces");

        // ── The list ─────────────────────────────────────────────────────────────────────────────

        public static string ColumnName => Pick("نام", "Name");

        public static string ColumnSlug => Pick("اسلاگ", "Slug");

        public static string ColumnPlan => Pick("پلن", "Plan");

        public static string ColumnMembers => Pick("کاربران", "Users");

        public static string ColumnStorage => Pick("مصرف / سقف", "Used / cap");

        public static string ColumnFiles => Pick("فایل", "Files");

        public static string NoTenants => Pick(
            "هنوز هیچ فضای کاری‌ای ساخته نشده است. اولین مشتری را با فرم بالا اضافه کنید.",
            "No workspace has been made yet. Add the first customer with the form above.");

        /// <summary>Spent against the cap. Both halves arrive already formatted as byte quantities.</summary>
        [VerbatimText("both halves are already-formatted byte quantities, which stay Latin in either language")]
        public static string OfCap(string used, string cap) => Pick($"{used} / {cap}", $"{used} / {cap}");

        public static string MembersOfCap(long used, long cap) => Pick(
            $"{Numerals.Count(used)} از {Numerals.Count(cap)}",
            $"{Numerals.Count(used)} of {Numerals.Count(cap)}");

        public static string FileCount(int files) => Pick(
            $"{Numerals.Count(files)} فایل",
            files == 1 ? "1 file" : $"{Numerals.Count(files)} files");

        public static string NoPlan => Pick("بدون پلن", "No tier");

        // ── Making one ───────────────────────────────────────────────────────────────────────────

        public static string CreateHeading => Pick("فضای کاری تازه", "A new workspace");

        public static string NameField => Pick("نام فضای کاری", "Workspace name");

        public static string NameHint => Pick(
            "همان چیزی که مشتری خودش را با آن می‌شناسد. هر وقت خواستید عوض می‌شود.",
            "Whatever the customer calls themselves. It can be changed whenever you like.");

        public static string SlugField => Pick("اسلاگ", "Slug");

        /// <summary>
        /// The rule, said before it is enforced. A form that refuses without stating the rule makes
        /// the operator guess at a value they are about to be stuck with.
        /// </summary>
        /// <param name="minimum">Read from <c>TenantSlug</c> rather than transcribed, so the sentence
        /// keeps telling the truth if the rule moves.</param>
        public static string SlugRule(int minimum, int maximum) => Pick(
            $"فقط حروف کوچک انگلیسی، رقم و خط تیره؛ بین {Numerals.Plain(minimum)} تا "
            + $"{Numerals.Plain(maximum)} نویسه؛ خط تیره نه در ابتدا، نه در انتها و نه دوتایی.",
            $"Lowercase Latin letters, digits and hyphens only; {minimum} to {maximum} characters; "
            + "no hyphen at the start, at the end, or doubled.");

        /// <summary>
        /// The warning that matters more than the rule: this value is permanent because the files
        /// are already under it. Said at the field, not in a document.
        /// </summary>
        public static string SlugIsPermanent => Pick(
            "این اسلاگ نام پوشه‌ی این مشتری داخل هر اکانت ذخیره‌سازی است. بعد از اولین آپلود دیگر "
            + "عوض نمی‌شود: تغییر آن فایل‌های قبلی را در پوشه‌ی قدیمی جا می‌گذارد و هیچ‌چیز در محصول "
            + "نمی‌داند آن دو پوشه به هم مربوط‌اند. با حوصله انتخابش کنید.",
            "This slug is the customer's folder name inside every storage account. It cannot be "
            + "changed once anything has been uploaded: renaming it would leave the earlier files in "
            + "the old folder, and nothing in the product would know the two are related. Choose it "
            + "carefully.");

        public static string PlanField => Pick("پلن", "Plan");

        public static string PlanHint => Pick(
            "چهار عدد این پلن روی ردیف همین فضای کاری نوشته می‌شود. بعداً از صفحه‌ی پلن‌ها قابل تغییر است.",
            "The tier's four numbers are copied onto this workspace's own row. They can be changed "
            + "later from the plans screen.");

        public static string PlanDefault => Pick("پیش‌فرض", "The default");

        public static string Create => Pick("ساخت فضای کاری", "Create the workspace");

        // ── One workspace ────────────────────────────────────────────────────────────────────────

        public static string WorkspaceHeading(string workspace) => Pick(
            $"فضای کاری {workspace}",
            $"Workspace {workspace}");

        public static string BackToTenants => Pick("بازگشت به فضاهای کاری", "Back to the workspaces");

        public static string SlugLabel => Pick("پوشه‌ی ذخیره‌سازی", "Storage folder");

        public static string CreatedLabel => Pick("ساخته شده", "Created");

        public static string PlanLabel => Pick("پلن", "Plan");

        public static string StorageLabel => Pick("فضای مصرفی", "Storage used");

        public static string SeatsLabel => Pick("کاربران", "Users");

        public static string ManagePlan => Pick("تغییر پلن و سهمیه", "Change the plan and quotas");

        public static string QuotaHistoryHeading => Pick("تاریخچه‌ی سهمیه", "Quota history");

        public static string QuotaHistoryEmpty => Pick(
            "بعد از ساخت، هنوز چیزی تغییر نکرده است.",
            "Nothing has changed since it was created.");

        public static string ColumnWhen => Pick("زمان", "When");

        public static string ColumnChange => Pick("تغییر", "Change");

        public static string ColumnReason => Pick("دلیل", "Reason");

        /// <summary>
        /// One history row's move, both halves already formatted for their dimension. The arrow
        /// turns with the script: in Persian the reading order is right to left, so an arrow drawn
        /// left points from the old value to the new one.
        /// </summary>
        public static string FromTo(string from, string to) => Pick($"{from} ← {to}", $"{from} → {to}");

        // ── The people in it ─────────────────────────────────────────────────────────────────────

        public static string MembersHeading => Pick("کاربران این فضای کاری", "The people in this workspace");

        public static string ColumnEmail => Pick("ایمیل", "Email");

        public static string ColumnDisplayName => Pick("نام", "Name");

        public static string ColumnStatus => Pick("وضعیت", "Status");

        public static string ColumnAdded => Pick("افزوده", "Added");

        public static string StatusActive => Pick("فعال", "Active");

        public static string StatusDisabled => Pick("غیرفعال", "Disabled");

        public static string NoMembers => Pick(
            "هنوز کسی در این فضای کاری حساب ندارد. تا وقتی حسابی ساخته نشود، هیچ‌کس نمی‌تواند وارد شود.",
            "Nobody has an account in this workspace yet. Until one is made, nobody can sign in.");

        public static string Disable => Pick("غیرفعال کردن", "Disable");

        public static string Enable => Pick("فعال کردن", "Enable");

        public static string ResetPassword => Pick("گذرواژه‌ی تازه", "New password");

        public static string DisableExplanation => Pick(
            "غیرفعال کردن، همان درخواست بعدی را رد می‌کند — نه ورود بعدی. نشستِ باز همان لحظه بسته "
            + "می‌شود و حساب باقی می‌ماند، پس فعال کردن دوباره یک کلیک است.",
            "Disabling refuses the next request, not the next sign-in: an open session ends there and "
            + "then. The account stays, so re-enabling is one click.");

        public static string AddMemberHeading => Pick("افزودن کاربر", "Add a person");

        public static string EmailField => Pick("ایمیل", "Email");

        public static string DisplayNameField => Pick("نام نمایشی", "Display name");

        public static string DisplayNameHint => Pick("اختیاری.", "Optional.");

        public static string PasswordField => Pick("گذرواژه‌ی اولیه", "Initial password");

        public static string PasswordHint => Pick(
            "شما آن را تعیین می‌کنید و خودتان به کاربر می‌رسانید — این محصول ایمیلی نمی‌فرستد و "
            + "بازنشانی گذرواژه از داخل پنل ندارد. بعد از ذخیره دیگر خوانده نمی‌شود.",
            "You set it and you hand it over yourself — this product sends no email and has no "
            + "self-service password reset. It cannot be read back once saved.");

        public static string AddMember => Pick("ساخت حساب", "Create the account");

        /// <summary>
        /// Shown where the add form would be, when there is no seat for it. The control is absent
        /// rather than greyed: a cap is a condition the operator can change, and the sentence names
        /// the change.
        /// </summary>
        public static string SeatsFullHere => Pick(
            "این فضای کاری به سقف کاربرانش رسیده است. برای افزودن کاربر تازه، اول سقف اعضا را از "
            + "صفحه‌ی پلن بالا ببرید.",
            "This workspace is at its user limit. To add another person, raise the seat limit on the "
            + "plan screen first.");

        // ── Why there is no delete button ────────────────────────────────────────────────────────

        public static string NoDeletionHeading => Pick(
            "فضای کاری حذف نمی‌شود",
            "A workspace is not deleted");

        /// <summary>
        /// Said on the screen, because the first thing an operator does when a button is missing is
        /// look for it, and the second is open a database client.
        /// </summary>
        public static string NoDeletionBody => Pick(
            "فایل‌های این مشتری واقعاً داخل اکانت‌های ذخیره‌سازی وجود دارند و این ردیف تنها چیزی است "
            + "که آن‌ها را نام می‌برد؛ با حذف آن، بایت‌ها می‌مانند و دیگر هیچ راهی به آن‌ها نیست. "
            + "اگر همکاری با مشتری تمام شده، همه‌ی کاربرانش را غیرفعال کنید: نتیجه‌اش همان است و "
            + "برگشت‌پذیر است.",
            "This customer's files really exist inside the storage accounts, and this row is the only "
            + "thing that names them — delete it and the bytes stay behind with nothing left to reach "
            + "them by. If the customer is finished, disable everybody in the workspace instead: it "
            + "achieves the same thing and it can be undone.");

        // ── What the commands say afterwards ─────────────────────────────────────────────────────

        public static string TenantCreated(string workspace) => Pick(
            $"فضای کاری «{workspace}» ساخته شد. حالا اولین کاربرش را بسازید.",
            $"The «{workspace}» workspace was created. Now make its first account.");

        public static string NameRequired => Pick(
            "نام فضای کاری را وارد کنید (حداکثر ۲۰۰ نویسه).",
            "Enter a workspace name (200 characters at most).");

        public static string SlugMalformed => Pick(
            "این اسلاگ پذیرفته نشد. اسلاگ نام پوشه است، پس قاعده‌اش سخت‌گیرانه است:",
            "That slug was not accepted. A slug is a folder name, so the rule is strict:");

        public static string SlugTaken(string slug) => Pick(
            $"اسلاگ «{slug}» قبلاً برای فضای کاری دیگری گرفته شده است. دو فضای کاری نمی‌توانند یک "
            + "پوشه داشته باشند؛ اسلاگ دیگری بگذارید.",
            $"The slug «{slug}» already belongs to another workspace. Two workspaces cannot share one "
            + "folder — choose a different slug.");

        public static string PlanNotFound => Pick(
            "این پلن پیدا نشد یا بازنشسته است، پس فضای کاری ساخته نشد.",
            "That plan was not found, or it is retired, so the workspace was not created.");

        public static string TenantNotFound => Pick(
            "این فضای کاری پیدا نشد.",
            "That workspace was not found.");

        public static string MemberCreated(string email) => Pick(
            $"حساب {email} ساخته شد. گذرواژه را خودتان به او برسانید؛ اینجا دیگر نشان داده نمی‌شود.",
            $"The account {email} was created. Hand the password over yourself — it is not shown again.");

        public static string EmailRequired => Pick(
            "ایمیل کاربر را وارد کنید.",
            "Enter the person's email address.");

        public static string PasswordRequired => Pick(
            "گذرواژه‌ی اولیه را وارد کنید.",
            "Enter an initial password.");

        /// <summary>
        /// The cap refusing, with both figures in it. It says what to do next, because "no" with no
        /// next step is what makes an operator open a database client.
        /// </summary>
        public static string SeatsFull(int used, int cap) => Pick(
            $"این فضای کاری {Numerals.Count(used)} کاربر از {Numerals.Count(cap)} کاربر مجازش دارد، "
            + "پس حسابی ساخته نشد. اول سقف اعضا را از صفحه‌ی پلن بالا ببرید.",
            $"This workspace has {Numerals.Count(used)} of its {Numerals.Count(cap)} permitted users, "
            + "so no account was created. Raise the seat limit on the plan screen first.");

        public static string MemberNotFound => Pick(
            "این کاربر در این فضای کاری پیدا نشد.",
            "That person is not in this workspace.");

        public static string MemberDisabled(string email) => Pick(
            $"{email} غیرفعال شد. اگر همین حالا وارد پنل بود، درخواست بعدی‌اش رد می‌شود.",
            $"{email} is disabled. If they were in the panel, their next request is refused.");

        public static string MemberEnabled(string email) => Pick(
            $"{email} دوباره فعال شد و می‌تواند با همان گذرواژه وارد شود.",
            $"{email} is enabled again and can sign in with the same password.");

        public static string PasswordWasReset(string email) => Pick(
            $"گذرواژه‌ی {email} عوض شد. نشست‌های بازش بسته شد و باید دوباره وارد شود.",
            $"The password for {email} was changed. Their open sessions ended and they have to sign "
            + "in again.");

        /// <summary>Identity's own refusal, already in this language, wrapped in ours.</summary>
        public static string Refused(string why) => Pick(
            $"انجام نشد: {why}",
            $"That did not happen: {why}");
    }
}
