namespace DriveUnion.Web.Localization;

/// <summary>
/// Every word the panel says, in both languages, in one table.
///
/// <para><b>Why this and not <c>.resx</c> + <c>IStringLocalizer</c>.</b> The framework's own
/// mechanism was the obvious candidate and was rejected on three counts, all of them about how it
/// fails rather than how it works:</para>
/// <list type="number">
/// <item><c>L["Nav.Fils"]</c> compiles. A mistyped key, a renamed key and a key that was never
/// written all behave identically at run time: the localiser returns the key itself and the panel
/// ships <c>Nav.Fils</c> to a paying customer. Here a key is a member, a typo is a build error, and
/// the next agent migrating a screen cannot invent a string that does not exist.</item>
/// <item>A placeholder in a <c>.resx</c> is a positional <c>{0}</c> that nothing checks. Here a
/// string with a placeholder is a method, and its arity and types are the compiler's business.</item>
/// <item>What <c>.resx</c> buys — satellite assemblies, translator tooling, a hundred cultures —
/// buys this product nothing. It has two languages, written by the same people who write the code,
/// and its Persian is careful enough that a translator handed a spreadsheet of fragments would do
/// worse. XML also hides zero-width non-joiners, which Persian is full of.</item>
/// </list>
///
/// <para><b>How this relates to the public download page.</b> That page has its own
/// <c>PublicText.Pick(fa, en)</c> written inline in the view, and it stays there for now — see
/// Localization/README.md for what folding it in costs and why it is a separate change. The two are
/// the same idea at different scales: <c>Pick</c> at a call site is fine for one chrome-less card
/// with a dozen strings and is unusable for a whole panel, which is why the pairs live in this table
/// and the views name entries. The resolution rules are already reconciled — the switch below writes
/// the framework's standard culture cookie at <c>Path=/</c>, so it reaches <c>/d/{slug}</c> too.</para>
///
/// <para>Adding a string: see Localization/README.md. The short version is that both languages are
/// supplied at the same line, so an entry that exists in one culture and not the other cannot be
/// written; <c>LocalizationCatalogueTests</c> proves the rest.</para>
/// </summary>
public static class UiText
{
    /// <summary>
    /// The one place the language is branched on. Both sides are evaluated, which costs nothing for
    /// a literal and keeps every entry readable as the pair it is.
    /// </summary>
    private static string Pick(string fa, string en) => PanelCulture.IsPersian ? fa : en;

    public static class Brand
    {
        public static string Name => Pick("درایو یونیون", "Drive Union");

        /// <summary>The letter in the 30×30 brand square.</summary>
        public static string Mark => Pick("د", "D");

        /// <summary>The document title of a page that has one of its own.</summary>
        public static string PageTitle(string title) => Pick($"{title} · درایو یونیون", $"{title} · Drive Union");
    }

    public static class Shell
    {
        public static string NavigationLabel => Pick("ناوبری اصلی", "Main navigation");

        public static string Dashboard => Pick("داشبورد", "Dashboard");

        public static string Files => Pick("فایل‌ها", "Files");

        public static string TransferQueue => Pick("صف انتقال", "Transfer queue");

        public static string ShareLinks => Pick("لینک‌های اشتراک", "Share links");

        public static string GoogleAccounts => Pick("اکانت‌های گوگل", "Google accounts");

        public static string Settings => Pick("تنظیمات", "Settings");

        /// <summary>The operator's bot configuration at <c>/telegram</c>.</summary>
        public static string TelegramBot => Pick("ربات تلگرام", "Telegram bot");

        /// <summary>The customer's own account link at <c>/telegram/link</c>.</summary>
        public static string Telegram => Pick("تلگرام", "Telegram");

        /// <summary>On the nav items that have no controller yet, so a click is not a 404.</summary>
        public static string NextRelease => Pick("در نسخه‌ی بعدی", "In the next release");

        public static string TodaysUploadQuota => Pick("سهمیه آپلود امروز", "Today's upload quota");

        public static string DesignGuide => Pick("راهنمای طراحی ↗", "Design guide ↗");

        /// <summary>The avatar when there is no name to take an initial from.</summary>
        public static string UnknownInitial => Pick("؟", "?");

        public static string Menu => Pick("منو", "Menu");

        public static string SearchEveryAccount => Pick("جست‌وجو در همه‌ی اکانت‌ها", "Search every account");

        public static string SearchFiles => Pick("جست‌وجو در فایل‌ها", "Search files");

        public static string ThemeNeedsScript =>
            Pick("تم تیره نیاز به جاوااسکریپت دارد", "Dark mode needs JavaScript");

        public static string Upload => Pick("آپلود فایل", "Upload file");

        public static string SignOut => Pick("خروج", "Sign out");

        public static string SignOutTitle => Pick("خروج از حساب", "Sign out of this account");

        public static string RoleOperator => Pick("اپراتور", "Operator");

        public static string RoleUser => Pick("کاربر", "User");

        /// <summary>
        /// The label on the language switch: the language you are not reading, named in its own
        /// script. «English» never becomes «انگلیسی» — somebody who cannot read this page has to be
        /// able to recognise the way out of it.
        /// </summary>
        [VerbatimText("each language names itself, so the reader of neither can still find the switch")]
        public static string LanguageSwitch => Pick("English", "فارسی");

        public static string LanguageSwitchTitle =>
            Pick("نمایش پنل به انگلیسی", "Show the panel in Persian");
    }

    public static class Identity
    {
        public static string SignInTitle => Pick("ورود", "Sign in");

        public static string SignInHeading => Pick("ورود به پنل", "Sign in to the panel");

        public static string SignInSubtitle => Pick(
            "حساب‌ها را اپراتور می‌سازد؛ ثبت‌نام عمومی وجود ندارد.",
            "Accounts are created by the operator; there is no public sign-up.");

        public static string Email => Pick("ایمیل", "Email");

        public static string Password => Pick("گذرواژه", "Password");

        public static string RememberMe => Pick("مرا به خاطر بسپار", "Keep me signed in");

        public static string NoPasswordReset => Pick(
            "بازنشانی گذرواژه از داخل پنل ممکن نیست. اگر گذرواژه‌تان را ندارید، از اپراتور بخواهید.",
            "The panel cannot reset a password. If you do not have yours, ask the operator for it.");

        /// <summary>
        /// One sentence for a wrong password and for an address with no account. The difference
        /// between two answers is a list of who has an account here.
        /// </summary>
        public static string BadCredentials => Pick(
            "ایمیل یا گذرواژه درست نیست.",
            "That email address and password do not match.");

        public static string LockedOut => Pick(
            "به دلیل تلاش‌های ناموفق پیاپی، این حساب موقتاً قفل شده است. کمی بعد دوباره تلاش کنید.",
            "Too many failed attempts, so this account is locked for a while. Try again shortly.");

        public static string SetupTitle => Pick("راه‌اندازی اولیه", "First-run setup");

        public static string SetupHeading => Pick(
            "این پنل هنوز اپراتور ندارد",
            "This panel has no operator yet");

        public static string SetupSubtitle => Pick(
            "اولین حسابی که اینجا ساخته شود اپراتور پنل است: صاحب اکانت‌های گوگل و همه‌ی فضاهای کاری.",
            "The first account made here is the panel's operator: the owner of the Google accounts "
            + "and of every workspace.");

        public static string OperatorEmail => Pick("ایمیل اپراتور", "Operator email");

        public static string RepeatPassword => Pick("تکرار گذرواژه", "Repeat the password");

        public static string PasswordRulesHeading => Pick(
            "گذرواژه باید این‌ها را داشته باشد:",
            "The password must have:");

        public static string CreateOperator => Pick(
            "ساخت حساب اپراتور و ورود",
            "Create the operator account and sign in");

        public static string SetupHappensOnce => Pick(
            "این صفحه فقط تا وقتی هست که پنل هیچ اپراتوری نداشته باشد. بعد از ساخته‌شدن حساب، همین نشانی "
            + "دیگر باز نمی‌شود و حساب‌های بعدی را اپراتور می‌سازد. بازنشانی گذرواژه هم در پنل وجود ندارد، "
            + "پس گذرواژه را جایی امن نگه دارید.",
            "This page exists only while the panel has no operator. Once the account is made this "
            + "address stops answering, and every account after it is made by the operator. There is "
            + "no password reset in the panel either, so keep this password somewhere safe.");

        public static string HaveAnAccountAlready => Pick(
            "حساب دارید و اپراتور نیستید؟",
            "Have an account, and are not the operator?");

        public static string GoToSignIn => Pick("ورود به پنل", "Sign in to the panel");

        public static string DevelopmentOnly => Pick("فقط در محیط توسعه", "Development only");

        public static string MadeInThisBrowser => Pick("ساخته‌شده در همین مرورگر", "Made in this browser");

        public static string SuggestionNote => Pick(
            "این یک پیشنهاد است، نه مقدار پیش‌فرض. تا وقتی دکمه‌اش را نزنید در هیچ کادری نوشته نمی‌شود. "
            + "جایی ذخیره نمی‌شود، پس اگر استفاده‌اش کردید همین حالا نگهش دارید.",
            "This is a suggestion, not a default. It goes into no box until you press the button, and "
            + "it is stored nowhere — if you use it, keep it now.");

        public static string UseSuggestion => Pick("گذاشتن در کادرها", "Put it in both boxes");

        public static string CopySuggestion => Pick("کپی", "Copy");

        public static string NewSuggestion => Pick("پیشنهاد تازه", "Suggest another");

        public static string SuggestionSelected => Pick(
            "انتخاب شد؛ با Ctrl+C کپی کنید.",
            "Selected — press Ctrl+C to copy.");

        public static string SuggestionFilled => Pick("در هر دو کادر گذاشته شد.", "Put in both boxes.");

        public static string SuggestionCopied => Pick("کپی شد.", "Copied.");

        public static string PasswordMinimumLength(int characters) => Pick(
            $"دست‌کم {Numerals.Plain(characters)} نویسه",
            $"at least {characters} characters");

        public static string PasswordUppercase => Pick(
            "دست‌کم یک حرف بزرگ لاتین (A تا Z)",
            "at least one uppercase Latin letter (A to Z)");

        public static string PasswordLowercase => Pick(
            "دست‌کم یک حرف کوچک لاتین (a تا z)",
            "at least one lowercase Latin letter (a to z)");

        public static string PasswordDigit => Pick("دست‌کم یک رقم (۰ تا ۹)", "at least one digit (0 to 9)");

        public static string PasswordSymbol => Pick(
            "دست‌کم یک نشانه، مانند ! یا # یا ?",
            "at least one symbol, such as !, # or ?");

        public static string PasswordDistinctCharacters(int characters) => Pick(
            $"دست‌کم {Numerals.Plain(characters)} نویسه‌ی متفاوت",
            $"at least {characters} different characters");

        public static string SignOutTitle => Pick("خروج", "Sign out");

        public static string SignOutHeading => Pick("خروج از حساب", "Sign out of this account");

        public static string SignOutExplanation => Pick(
            "با خروج، این نشست بسته می‌شود و برای باز کردن پنل باید دوباره وارد شوید.",
            "Signing out ends this session; opening the panel again means signing in again.");

        public static string NoAccessTitle => Pick("دسترسی ندارید", "No access");

        public static string NoWorkspaceTitle => Pick("بدون فضای کاری", "No workspace");

        public static string NoWorkspaceHeading => Pick(
            "حساب شما هنوز به فضای کاری وصل نیست",
            "Your account is not attached to a workspace yet");

        public static string NoWorkspaceSubtitle => Pick(
            "ورود انجام شد، ولی این حساب هنوز جایی برای کار کردن ندارد.",
            "You are signed in, but this account has nowhere to work yet.");

        public static string NoWorkspaceBody => Pick(
            "تا وقتی اپراتور این حساب را به یک فضای کاری وصل نکند، صفحه‌های پنل باز نمی‌شوند. "
            + "این خطای شما نیست و با ورود دوباره درست نمی‌شود.",
            "Until the operator attaches this account to a workspace, none of the panel's pages will "
            + "open. This is not your mistake, and signing in again will not change it.");

        public static string OperatorHasNoWorkspaceHeading => Pick(
            "این صفحه متعلق به یک فضای کاری است",
            "This page belongs to a workspace");

        public static string OperatorHasNoWorkspaceSubtitle => Pick(
            "حساب شما اپراتور است و به هیچ فضای کاری تعلق ندارد.",
            "Your account is an operator account, and belongs to no workspace.");

        public static string GoToGoogleAccounts => Pick("رفتن به اکانت‌های گوگل", "Go to Google accounts");

        public static string ForbiddenHeading => Pick(
            "این بخش در دسترس شما نیست",
            "This part of the panel is not available to you");

        public static string ForbiddenSubtitle => Pick(
            "حساب شما اجازه‌ی باز کردن این صفحه را ندارد.",
            "Your account is not allowed to open this page.");

        public static string BackToFiles => Pick("بازگشت به فایل‌ها", "Back to files");

        public static string SignedInAsSomebodyElse => Pick(
            "با حساب دیگری وارد شده‌اید؟",
            "Signed in as somebody else?");
    }

    public static class Files
    {
        public static string Title => Pick("فایل‌ها", "Files");

        /// <summary>The count beside the heading. English agrees with its noun; Persian does not.</summary>
        public static string FileCount(int files) => Pick(
            $"{Numerals.Count(files)} فایل",
            files == 1 ? "1 file" : $"{Numerals.Count(files)} files");

        public static string ColumnName => Pick("نام", "Name");

        public static string ColumnSize => Pick("حجم", "Size");

        public static string ColumnModified => Pick("تغییر", "Modified");

        public static string ColumnLinks => Pick("لینک", "Links");

        public static string EmptyStateHeading => Pick(
            "هنوز فایلی آپلود نشده است.",
            "Nothing has been uploaded yet.");

        public static string EmptyStateAction => Pick("آپلود اولین فایل", "Upload the first file");

        /// <summary>The links cell: how many live links this file has.</summary>
        public static string LinkCount(int links) => Pick(
            $"{Numerals.Count(links)} لینک",
            links == 1 ? "1 link" : $"{Numerals.Count(links)} links");

        public static string Delete => Pick("حذف", "Delete");

        public static string DetailEmptyState => Pick(
            "برای دیدن جزئیات، یک فایل را انتخاب کنید.",
            "Select a file to see its details.");

        public static string CreatedLabel => Pick("ساخته شده", "Created");

        public static string ActiveLinkHeading => Pick("لینک فعال", "Active link");

        /// <summary>The badge on a link anybody with the address can use.</summary>
        public static string PublicBadge => Pick("عمومی", "Public");

        public static string Copy => Pick("کپی", "Copy");

        public static string RevokeLink => Pick("ابطال لینک", "Revoke link");

        public static string OpenPublicPage => Pick("دیدن صفحه عمومی", "Open the public page");

        public static string NoPublicLink => Pick(
            "این فایل هنوز لینک عمومی ندارد.",
            "This file has no public link yet.");

        public static string CreateLink => Pick("ساخت لینک", "Create a link");

        public static string BackToFiles => Pick("بازگشت به فایل‌ها", "Back to files");

        public static string UploadTitle => Pick("آپلود فایل", "Upload a file");

        public static string UploadSubtitle => Pick(
            "فایل از مرورگر به سرور ما می‌رود و از آنجا تکه‌تکه به فضای ذخیره‌سازی منتقل می‌شود.",
            "The file goes from your browser to our server, and from there to storage a chunk at a time.");

        public static string UploadNeedsScript => Pick(
            "آپلود تکه‌ای بدون جاوااسکریپت ممکن نیست — یک فایل ۹۶ گیگابایتی باید در تکه‌های قابل ازسرگیری فرستاده شود.",
            "A chunked upload needs JavaScript — a 96 GB file has to be sent in resumable pieces.");

        public static string Deleted => Pick("فایل حذف شد.", "The file was deleted.");

        public static string NotFound => Pick("این فایل پیدا نشد.", "That file was not found.");

        public static string LinkCreated(string url) => Pick(
            $"لینک ساخته شد: {url}",
            $"Link created: {url}");

        public static string LinkRevoked => Pick("لینک ابطال شد.", "The link was revoked.");

        public static string LinkNotFound => Pick("این لینک پیدا نشد.", "That link was not found.");

        /// <summary>The detail panel's download line for a link with a cap on it.</summary>
        public static string DownloadsOfCap(long count, long cap) => Pick(
            $"{Numerals.Count(count)} / {Numerals.Count(cap)} دانلود",
            $"{Numerals.Count(count)} / {Numerals.Count(cap)} downloads");

        public static string Downloads(long count) => Pick(
            $"{Numerals.Count(count)} دانلود",
            count == 1 ? "1 download" : $"{Numerals.Count(count)} downloads");

        public static string NoExpiry => Pick("بدون انقضا", "No expiry");

        public static string Expired => Pick("منقضی", "Expired");

        public static string ExpiresInDays(int days) => Pick(
            $"انقضا {Numerals.Plain(days)} روز",
            days == 1 ? "Expires in 1 day" : $"Expires in {days} days");
    }

    public static class Links
    {
        public static string Title => Pick("لینک‌های اشتراک", "Share links");

        public static string LinkCount(int links) => Pick(
            $"{Numerals.Count(links)} لینک",
            links == 1 ? "1 link" : $"{Numerals.Count(links)} links");

        public static string ColumnFile => Pick("فایل", "File");

        public static string ColumnAddress => Pick("آدرس", "Address");

        public static string ColumnDownloads => Pick("دانلود", "Downloads");

        public static string ColumnExpiry => Pick("انقضا", "Expires");

        public static string ColumnStatus => Pick("وضعیت", "Status");

        public static string EmptyStateHeading => Pick(
            "هنوز لینکی ساخته نشده است.",
            "No link has been created yet.");

        public static string EmptyStateAction => Pick("رفتن به فایل‌ها", "Go to the files");

        public static string StatusActive => Pick("فعال", "Active");

        public static string StatusNearCap => Pick("نزدیک سقف", "Near the limit");

        public static string StatusCapReached => Pick("سقف تکمیل", "Limit reached");

        public static string StatusExpired => Pick("منقضی", "Expired");

        /// <summary>A link the customer revoked. It is not merely inactive — somebody turned it off.</summary>
        public static string StatusRevoked => Pick("غیرفعال", "Revoked");

        /// <summary>The downloads cell: spent against the cap the customer set.</summary>
        public static string DownloadsOfCap(long count, long cap) => Pick(
            $"{Numerals.Count(count)}/{Numerals.Count(cap)}",
            $"{Numerals.Count(count)}/{Numerals.Count(cap)}");

        /// <summary>The same cell with no cap. «∞» is a symbol and has no language.</summary>
        public static string DownloadsUncapped(long count) => Pick(
            $"{Numerals.Count(count)}/∞",
            $"{Numerals.Count(count)}/∞");

        public static string ExpiryNone => Pick("بدون", "None");

        public static string ExpiryDays(int days) => Pick(
            $"{Numerals.Plain(days)} روز",
            days == 1 ? "1 day" : $"{days} days");
    }

    public static class Accounts
    {
        public static string Title => Pick("اکانت‌های گوگل", "Google accounts");

        public static string Subtitle => Pick(
            "توکن‌ها رمزنگاری‌شده روی سرور ذخیره می‌شوند.",
            "Tokens are stored encrypted on the server.");

        public static string AddAccount => Pick("+ افزودن اکانت با OAuth", "+ Add an account with OAuth");

        /// <summary>
        /// The primary action when nothing is configured, and the setup panel's own title. One entry
        /// on purpose: the button promises the panel, so the two cannot drift apart.
        /// </summary>
        public static string SetUpGoogle => Pick("راه‌اندازی اتصال به گوگل", "Set up the Google connection");

        /// <summary>
        /// The refusal, said where the refusal happens. It is two entries because the term
        /// <c>Client ID</c> sits between them in its own monospace, LTR box — a name Google spells
        /// this way in both languages — and the two halves land on different sides of it in Persian
        /// and in English. Neither half carries the space around the term; the punctuation that
        /// follows it does.
        /// </summary>
        public static string ConsentNeedsClientBefore => Pick(
            "گوگل بدون ",
            "Google turns down every request that arrives without a ");

        public static string ConsentNeedsClientAfter => Pick(
            " هیچ درخواستی را نمی‌پذیرد، و این کلاینت فقط در پروژه‌ی Google Cloud خودِ شما ساخته "
            + "می‌شود؛ این پنل نمی‌تواند آن را بسازد. دکمه شما را به مراحل ساخت و فرم ذخیره‌ی آن می‌برد.",
            ", and that client can only be made in your own Google Cloud project — this panel cannot "
            + "make it for you. The button takes you to the steps, and to the form that stores the result.");

        public static string EmptyConfigured => Pick(
            "هیچ اکانتی متصل نیست. تا وقتی یک اکانت متصل نشود، آپلود کار نمی‌کند.",
            "No account is connected. Until one is, uploading will not work.");

        public static string EmptyUnconfigured => Pick(
            "هنوز اکانتی متصل نیست. اولین اکانت پس از ذخیره‌ی کلاینت، از همین صفحه وصل می‌شود.",
            "No account is connected yet. Once the client is saved, the first one is connected from this page.");

        public static string StorageUsed => Pick("فضای مصرفی", "Storage used");

        public static string RefreshQuota => Pick("تازه‌سازی فضا", "Refresh storage");

        public static string Disconnect => Pick("قطع اتصال", "Disconnect");

        public static string StatusHealthy => Pick("سالم", "Healthy");

        public static string StatusPaused => Pick("متوقف", "Paused");

        public static string StatusDisconnected => Pick("قطع شده", "Disconnected");

        public static string SetupComplete => Pick(
            "کلاینت OAuth تنظیم شده است. برای تغییر یا بررسی، باز کنید.",
            "The OAuth client is configured. Open this to check or change it.");

        public static string SetupIncomplete => Pick(
            "مرحله‌های ساخت آن در Google Cloud، و نشانی‌ای که باید همان‌جا ثبت شود.",
            "How to make one in Google Cloud, and the address you have to register there.");

        public static string SetupReady => Pick("آماده", "Ready");

        public static string SetupUnfinished => Pick("ناقص", "Incomplete");

        /// <summary>Step 1, around the link to the console. See <see cref="ConsentNeedsClientBefore"/>.</summary>
        public static string StepProjectBefore => Pick("در ", "Create a new project in ");

        public static string StepProjectAfter => Pick(" یک پروژه‌ی جدید بسازید.", ".");

        public static string StepApiBefore => Pick("از بخش ", "Under ");

        public static string StepApiMiddle => Pick(" سرویس ", ", enable ");

        public static string StepApiAfter => Pick(" را فعال کنید.", ".");

        public static string StepClientBefore => Pick("در ", "Under ");

        public static string StepClientMiddle => Pick(" نوع ", ", choose the type ");

        public static string StepClientAfter => Pick(
            " را انتخاب کنید — نه Desktop و نه TV.",
            " — not Desktop and not TV.");

        public static string StepRedirectBefore => Pick(
            "در همان صفحه، این نشانی را دقیقاً در ",
            "On the same page, add this address, exactly as it stands, to ");

        public static string StepRedirectAfter => Pick(" اضافه کنید:", ":");

        public static string CopyUri => Pick("رونوشت", "Copy");

        public static string RedirectUriDiffers => Pick(
            "توجه: آدرس بازگشتی که هم‌اکنون اعمال می‌شود با نشانی بالا فرق دارد. همان که اعمال "
            + "می‌شود باید در گوگل ثبت شده باشد:",
            "Note: the redirect URI in force right now is not the address above. It is the one in "
            + "force that has to be registered with Google:");

        public static string StepConsentBefore => Pick(
            "صفحه‌ی رضایت (OAuth consent screen) را از حالت ",
            "Move the OAuth consent screen from ");

        public static string StepConsentMiddle => Pick(" به ", " to ");

        public static string StepConsentAfter => Pick(" ببرید.", ".");

        public static string StepConsentWhy => Pick(
            "تا وقتی در حالت Testing باشد، گوگل توکن تازه‌سازی را بعد از هفت روز باطل می‌کند و اتصال "
            + "اکانت درست یک هفته بعد از کار می‌افتد — بدون آنکه چیزی در پنل تغییر کرده باشد.",
            "While it stays in Testing, Google expires the refresh token after seven days, and the "
            + "account stops working exactly a week after it worked — with nothing in this panel "
            + "having changed.");

        public static string StepScopeBefore => Pick("دسترسی درخواستی ", "The scope this asks for is ");

        public static string StepScopeAfter => Pick(
            " است و گوگل آن را «restricted» می‌داند. چون فقط اکانت‌های خودِ شما وصل می‌شوند، یک‌بار "
            + "هشدار «unverified app» را می‌بینید و رد می‌کنید؛ مشتری‌ها هرگز این صفحه را نمی‌بینند.",
            ", which Google classes as restricted. Because only your own accounts are ever connected, "
            + "you meet the “unverified app” warning once and dismiss it; customers never see that screen.");

        public static string CurrentState => Pick("وضعیت فعلی", "What is in force");

        /// <summary>Never the secret itself — this is the whole of what a browser is ever told.</summary>
        public static string SecretStored => Pick("ذخیره شده", "Stored");

        public static string SourceConfiguration => Pick("از پیکربندی سرور", "From the server configuration");

        public static string SourcePanel => Pick("ذخیره‌شده در پنل", "Saved in the panel");

        public static string SourceUnset => Pick("تنظیم نشده", "Not set");

        /// <summary>
        /// The three settings a deployment can supply instead of the form, spelled exactly as it
        /// must spell them. Named once and rendered in three places, so a reader who meets it in the
        /// popup and again in the panel is reading the same three keys.
        /// </summary>
        [VerbatimText("configuration keys are the deployment's own spelling and a translated key sets nothing")]
        public static string ConfigurationKeys =>
            Pick("Google:ClientId · Google:ClientSecret · Google:RedirectUri",
                 "Google:ClientId · Google:ClientSecret · Google:RedirectUri");

        public static string ConfigurationOutranksBefore => Pick(
            "مقداری که در این فرم ذخیره کرده‌اید توسط پیکربندی سرور (",
            "What you saved in this form is overridden by the server's configuration (");

        public static string ConfigurationOutranksAfter => Pick(
            ") بازنویسی شده است. پیکربندی سرور همیشه اولویت دارد؛ برای اعمال مقدار این فرم باید آن "
            + "متغیر را از سرور بردارید.",
            "). The server configuration always wins; to put this form's value in force, remove that "
            + "setting from the server.");

        public static string EnvironmentAlternativeBefore => Pick(
            "همین سه مقدار را می‌توان به‌جای این فرم از پیکربندی سرور داد: ",
            "The same three values can come from the server's configuration instead of this form: ");

        public static string EnvironmentAlternativeAfter => Pick(
            " اگر آنجا مقداری باشد، همان اعمال می‌شود.",
            " If a value is set there, that is the one in force.");

        public static string Save => Pick("ذخیره", "Save");

        public static string SecretPlaceholder => Pick(
            "ذخیره شده — برای تغییر مقدار تازه وارد کنید",
            "Stored — type a new value to replace it");

        public static string RedirectUriHint => Pick(
            "باید مو‌به‌مو با آنچه در گوگل ثبت کرده‌اید یکی باشد.",
            "This has to match what you registered with Google, character for character.");

        /// <param name="when">Already in this language's own numerals — see <c>DisplayFormats.PanelDateTime</c>.</param>
        public static string LastChanged(string when) => Pick(
            $"آخرین تغییر: {when}",
            $"Last changed: {when}");

        public static string DeleteStored => Pick("حذف اطلاعات ذخیره‌شده", "Delete the stored client");

        public static string ClosePopup => Pick("بستن پنجره", "Close this window");

        public static string BackToAccounts => Pick("بازگشت به اکانت‌ها", "Back to the accounts");

        public static string PopupClosing => Pick(
            "این پنجره خودش بسته می‌شود…",
            "This window will close itself…");

        public static string SaveFailed => Pick(
            "ذخیره‌ی اطلاعات گوگل ناموفق بود. لاگ سرور را ببینید.",
            "Saving the Google client failed. Check the server log.");

        public static string SavedButOverridden => Pick(
            "اطلاعات ذخیره شد، اما پیکربندی سرور اولویت دارد و همان اعمال می‌شود.",
            "Saved — but the server configuration outranks it, and that is what is in force.");

        public static string Saved => Pick(
            "اطلاعات OAuth گوگل ذخیره شد. حالا می‌توانید اکانت را متصل کنید.",
            "The Google OAuth client is saved. You can connect an account now.");

        public static string Cleared => Pick(
            "اطلاعات OAuth ذخیره‌شده حذف شد.",
            "The stored OAuth client was deleted.");

        public static string NothingToClear => Pick(
            "چیزی برای حذف وجود نداشت.",
            "There was nothing to delete.");

        public static string ClearFailed => Pick(
            "حذف اطلاعات ذخیره‌شده ناموفق بود. لاگ سرور را ببینید.",
            "Deleting the stored client failed. Check the server log.");

        public static string ClientIdRequired => Pick(
            "شناسه‌ی کلاینت (Client ID) را وارد کنید.",
            "Enter the Client ID.");

        public static string ClientSecretRequired => Pick(
            "کلید محرمانه (Client Secret) را وارد کنید.",
            "Enter the Client Secret.");

        public static string RedirectUriRequired => Pick(
            "آدرس بازگشت (Redirect URI) را وارد کنید.",
            "Enter the Redirect URI.");

        public static string RedirectUriNotAbsolute => Pick(
            "آدرس بازگشت باید یک نشانی کامل با http یا https باشد.",
            "The redirect URI has to be a full address beginning with http or https.");

        public static string RedirectUriHasFragment => Pick(
            "آدرس بازگشت نباید بخش # داشته باشد.",
            "The redirect URI must not have a # fragment.");

        public static string RedirectUriNeedsHttps => Pick(
            "گوگل http را فقط برای localhost می‌پذیرد؛ برای بقیه‌ی آدرس‌ها https لازم است.",
            "Google accepts http for localhost only; every other address needs https.");

        public static string ConnectCancelledTitle => Pick("اتصال لغو شد", "Connection cancelled");

        public static string ConnectCancelled => Pick(
            "اتصال اکانت لغو شد.",
            "Connecting the account was cancelled.");

        public static string CallbackInvalidTitle => Pick("بازگشت نامعتبر", "Invalid return");

        public static string CallbackInvalid => Pick(
            "بازگشت از گوگل معتبر نبود. دوباره تلاش کنید.",
            "The return from Google was not valid. Try again.");

        public static string ExchangeFailedTitle => Pick(
            "تبادل با گوگل ناموفق بود",
            "The exchange with Google failed");

        public static string ExchangeFailed => Pick(
            "تبادل کد با گوگل ناموفق بود.",
            "Exchanging the code with Google failed.");

        public static string ConnectedTitle => Pick("اکانت متصل شد", "Account connected");

        public static string Connected => Pick(
            "اکانت گوگل متصل شد.",
            "The Google account is connected.");

        public static string UnconfiguredTitle => Pick(
            "پیکربندی گوگل کامل نیست",
            "Google is not fully configured");

        public static string Unconfigured => Pick(
            "پیکربندی OAuth گوگل کامل نیست. اطلاعات آن را در صفحه‌ی اکانت‌ها وارد کنید.",
            "The Google OAuth client is incomplete. Enter it on the accounts page.");

        public static string Disconnected => Pick(
            "اکانت قطع شد. فایل‌های موجود روی آن دست‌نخورده می‌مانند.",
            "The account is disconnected. The files already on it are left untouched.");

        public static string AccountNotFound => Pick("اکانت پیدا نشد.", "That account was not found.");

        public static string QuotaRefreshed => Pick(
            "فضای اکانت به‌روزرسانی شد.",
            "The account's storage figures are up to date.");

        public static string QuotaRefreshFailed => Pick(
            "به‌روزرسانی فضا ناموفق بود.",
            "Refreshing the storage figures failed.");
    }

    public static class Home
    {
        public static string ErrorTitle => Pick("خطا", "Error");

        public static string ErrorHeading => Pick("خطایی رخ داد", "Something went wrong");

        public static string ErrorBody => Pick(
            "درخواست شما کامل نشد. اگر دوباره تکرار شد، شناسه زیر را برای پشتیبانی بفرستید.",
            "Your request did not finish. If it happens again, send support the id below.");
    }

    /// <summary>
    /// The living style guide at <c>/design</c>.
    ///
    /// Only the words are here. Every hex value, <c>oklch()</c>, pixel size, CSS custom property and
    /// file path on that page is documentation of the stylesheet and stays a literal in the view: it
    /// is the same in both languages because it is not language. What the catalogue holds is the
    /// headings, the navigation and the prose that says what a rule is — plus the component
    /// specimens, which have to be readable in whichever language somebody is checking a pixel in.
    /// An English panel's labels are longer than the Persian ones, and a gallery that could only
    /// show Persian could not be used to find the column they overflow.
    /// </summary>
    public static class Design
    {
        public static string Title => Pick("راهنمای طراحی", "Design guide");

        public static string IntroBefore => Pick(
            "مرجع زنده‌ی توکن‌ها و اجزای پایه. بدون دیتابیس و بدون ورود کار می‌کند تا هرکسی بتواند یک "
            + "پیکسل را بسنجد. رنگ هر نمونه از ",
            "A living reference for the tokens and the base components. It needs no database and no "
            + "sign-in, so anybody can check a pixel. Every sample takes its colour from ");

        public static string IntroAfter => Pick(
            " می‌آید، پس همیشه همان چیزی است که مرورگر واقعاً می‌کشد.",
            ", so it is always what the browser actually paints.");

        public static string JumpToSection => Pick("پرش به بخش‌ها", "Jump to a section");

        public static string ChipColours => Pick("رنگ‌ها", "Colours");

        public static string ChipTypography => Pick("تایپوگرافی", "Typography");

        public static string ChipSpacing => Pick("اسپیسینگ و شعاع", "Spacing and radii");

        public static string ChipNumerals => Pick("اعداد فارسی", "Persian numerals");

        public static string ChipComponentsLight => Pick("اجزا — روشن", "Components — light");

        public static string ChipComponentsDark => Pick("اجزا — تیره", "Components — dark");

        public static string SectionColours => Pick("توکن‌های رنگ — هر دو تم", "Colour tokens — both themes");

        public static string ThemeLight => Pick("روشن", "Light");

        public static string ThemeDark => Pick("تیره", "Dark");

        public static string OklchNote1 => Pick("مقادیر ", "The ");

        public static string OklchNote2 => Pick(
            " عمداً همان‌طور مانده‌اند. سبزهای این پالت فقط در فضای ادراکی روشنایی‌شان را بین دو تم "
            + "حفظ می‌کنند؛ تبدیل به هگز آن را خراب می‌کند. در این پروژه Tailwind نیست و قرار هم نیست "
            + "باشد — طرح روی مقادیر خارج از مقیاس بنا شده (",
            " values are left exactly as they are, on purpose. The greens in this palette hold their "
            + "lightness across the two themes only in a perceptual space, and converting them to hex "
            + "breaks that. There is no Tailwind here and there is not going to be — the design is "
            + "built on off-scale values (");

        public static string OklchNote3 => Pick("، ", ", ");

        public static string OklchNote4 => Pick("، شعاع‌های ۹ تا ۲۰).", ", and radii from 9 to 20).");

        public static string SectionTypography => Pick("تایپوگرافی", "Typography");

        public static string ColumnScaleSize => Pick("اندازه", "Size");

        public static string ColumnScaleWeight => Pick("وزن", "Weight");

        public static string ColumnScaleRole => Pick("نقش", "Role");

        public static string ColumnScaleSample => Pick("نمونه", "Sample");

        public static string RolePageTitle => Pick("عنوان صفحه", "Page title");

        public static string RolePublicFileTitle => Pick(
            "عنوان فایل در صفحه‌ی عمومی",
            "File title on the public page");

        public static string RoleBrand => Pick("نام برند و عنوان پنل کناری", "Brand name and sidebar title");

        public static string RoleDetailTitle => Pick("عنوان پنل جزئیات", "Detail panel title");

        public static string RoleCardTitle => Pick("عنوان کارت", "Card title");

        public static string RoleRowTitle => Pick("عنوان ردیف اصلی", "Primary row title");

        public static string RoleRowLabel => Pick("برچسب ردیف", "Row label");

        public static string RoleTableBody => Pick("بدنه‌ی جدول", "Table body");

        public static string RoleMetadata => Pick("متادیتا", "Metadata");

        public static string RoleTableHead => Pick("هدر جدول و زیرنویس", "Table head and caption");

        public static string RoleMonoFinePrint => Pick("ریزنویس مونواسپیس", "Monospace fine print");

        public static string TypographyFoot => Pick(
            "خانواده‌ی اصلی وزیرمتن، self-host؛ مونواسپیس برای اعداد، شناسه‌ها، آدرس‌ها و سرعت‌ها.",
            "Vazirmatn as the primary family, self-hosted; monospace for figures, ids, addresses and speeds.");

        public static string SectionSpacing => Pick("اسپیسینگ و شکل", "Spacing and shape");

        public static string SpacingScale => Pick("مقیاس اسپیسینگ", "The spacing scale");

        public static string SpacingNoteBefore => Pick(
            "گپ گرید کارت‌ها ۱۴ · پدینگ کارت ۱۸ · پدینگ محتوا ",
            "Card grid gap 14 · card padding 18 · content padding ");

        public static string Radii => Pick("شعاع‌ها", "Radii");

        public static string RadiusSmallButton => Pick("دکمه‌ی کوچک", "Small button");

        public static string RadiusInput => Pick("ورودی/دکمه", "Input / button");

        public static string RadiusLargeButton => Pick("دکمه‌ی بزرگ", "Large button");

        public static string RadiusInnerCard => Pick("کارت داخلی", "Inner card");

        public static string RadiusStatCard => Pick("کارت آمار", "Stat card");

        public static string RadiusMainCard => Pick("کارت اصلی", "Main card");

        public static string RadiusPublicCard => Pick("کارت عمومی", "Public card");

        public static string RadiusBadge => Pick("بَج/چیپ", "Badge / chip");

        public static string RowPadComfortable => Pick(" راحت / ", " comfortable / ");

        public static string RowPadCompact => Pick(
            " فشرده · نوار پیشرفت ۶ ردیفی و ۸ کارتی · آواتار ۵۰٪.",
            " compact · progress bar 6px in a row and 8px in a card · avatar radius 50%.");

        public static string SectionNumerals => Pick("اعداد فارسی", "Persian numerals");

        public static string NumeralsIntro => Pick(
            "طرح روی یک صفحه هر دو دستگاه رقم را کنار هم می‌گذارد و این تصادفی نیست: رقمی که داخل متن "
            + "فارسی نشسته فارسی است، و رقمی که در یک خوانش فنی چپ‌به‌راست است (حجم، سرعت، تأخیر، "
            + "اسلاگ، شناسه‌ی درایو) لاتین می‌ماند — چون آن‌ها را کپی می‌کنند، grep می‌کنند و برای "
            + "پشتیبانی گوگل می‌خوانند. پس تبدیل برای هر مقدار جداگانه تصمیم گرفته می‌شود، نه یک‌بار "
            + "برای کل صفحه.",
            "The design puts both digit systems on one screen and that is not an accident: a digit "
            + "set inside Persian prose is Persian, and a digit in a left-to-right technical readout "
            + "(a size, a speed, a latency, a slug, a Drive id) stays Latin — because those are the "
            + "values somebody copies, greps and reads out to Google support. So the conversion is "
            + "decided per value, never once for a whole page.");

        public static string ColumnCall => Pick("فراخوانی", "Call");

        public static string ColumnOutput => Pick("خروجی", "Output");

        public static string ColumnWhere => Pick("کجا", "Where");

        public static string NumeralsCounts => Pick(
            "شمارش‌ها؛ جداکننده‌ی هزارگان ٬",
            "Counts; the thousands separator is ٬");

        /// <summary>
        /// A specimen and not a label: the row exists to show Persian prose with a Persian numeral
        /// sitting in it, which is the one thing an English rendering of it could not show.
        /// </summary>
        [VerbatimText("the specimen is the Persian sentence itself, so translating it removes the thing on show")]
        public static string NumeralsProseExample =>
            Pick("«۲۴۱ بار دانلود شده»", "«۲۴۱ بار دانلود شده»");

        public static string NumeralsYear => Pick("سال — بدون گروه‌بندی", "A year — never grouped");

        public static string NumeralsPercent => Pick(
            "درصد در متن، با علامت ٪",
            "A percentage in prose, with the ٪ sign");

        public static string NumeralsAssembled => Pick(
            "رشته‌ی از پیش ساخته‌شده — تاریخ، مدت",
            "An already-assembled string — a date, a duration");

        public static string NumeralsStayLatin => Pick(
            "لاتین می‌ماند؛ تبدیل نکنید",
            "Stays Latin; do not convert it");

        public static string NumeralsFoot => Pick(
            "وزیرمتن نسخه‌ی FD هم دارد که هر رقم لاتین را فارسی می‌کند — عمداً استفاده نشده، چون حجم و "
            + "اسلاگ را هم عوض می‌کرد.",
            "Vazirmatn ships an FD variant that turns every Latin digit Persian — deliberately unused, "
            + "because it would have turned the sizes and the slugs too.");

        public static string SectionComponentsLight => Pick("اجزا — تم روشن", "Components — light theme");

        public static string SectionComponentsDark => Pick("اجزا — تم تیره", "Components — dark theme");

        public static string ThemeNoteBefore => Pick(
            "همان اجزا دو بار رندر شده‌اند، هر بار داخل یک ",
            "The same components, rendered twice, each copy inside its own ");

        public static string ThemeNoteAfter => Pick(
            " خودش. هیچ‌چیز اینجا رنگ ثابت ندارد؛ اگر جزئی در تیره خراب شود، همین‌جا دیده می‌شود.",
            ". Nothing here carries a fixed colour; a component that breaks in the dark theme breaks "
            + "here, in view.");

        public static string Buttons => Pick("دکمه‌ها", "Buttons");

        public static string ButtonPrimary => Pick("دکمه‌ی اصلی", "Primary button");

        public static string ButtonOutline => Pick("دکمه‌ی خطی", "Outline button");

        public static string ButtonSmall => Pick("کوچک", "Small");

        public static string ButtonPreviewPublic => Pick(
            "پیش‌نمایش صفحه عمومی ↗",
            "Preview the public page ↗");

        public static string ButtonDisabled => Pick("غیرفعال", "Disabled");

        public static string ButtonDisabledOutline => Pick("غیرفعال خطی", "Disabled, outline");

        public static string DownloadFile => Pick("دانلود فایل", "Download file");

        public static string ButtonsNoteBefore => Pick(
            "شعاع‌ها: ۸ کوچک · ۹ استاندارد · ۱۰ بزرگ · ۱۲ فراخوان عمومی. فراخوان عمومی سایه‌ی ",
            "Radii: 8 small · 9 standard · 10 large · 12 public call to action. The public call to "
            + "action carries the shadow ");

        public static string ButtonsNoteAfter => Pick(" دارد.", ".");

        public static string BadgesAndChips => Pick("بَج‌ها و چیپ‌ها", "Badges and chips");

        public static string BadgeFailed => Pick("ناموفق", "Failed");

        public static string ChipAllAccounts => Pick("همه اکانت‌ها", "All accounts");

        public static string ChipWithLinksOnly => Pick("فقط لینک‌دار", "Linked only");

        public static string ChipLargerThan => Pick("بزرگ‌تر از ۱۰GB", "Larger than 10GB");

        public static string TableComfortable => Pick("جدول — چگالی راحت", "Table — comfortable density");

        public static string TableCompact => Pick("جدول — چگالی فشرده", "Table — compact density");

        public static string ColumnAccount => Pick("اکانت", "Account");

        /// <summary>
        /// A Persian file name in a name column, which is a rendering case rather than a label: the
        /// guide has to show an RTL run inside this cell whichever language the panel is in.
        /// </summary>
        [VerbatimText("the specimen is an RTL file name, which an English rendering of it would not be")]
        public static string SampleRtlFileName =>
            Pick("دفترچه-راهنما-نسخه۳.pdf", "دفترچه-راهنما-نسخه۳.pdf");

        public static string SampleToday => Pick("امروز ۱۰:۲۲", "Today 10:22");

        public static string SampleYesterday => Pick("دیروز", "Yesterday");

        public static string SampleDaysAgo(int days) => Pick(
            $"{Numerals.Plain(days)} روز پیش",
            days == 1 ? "1 day ago" : $"{days} days ago");

        public static string SampleWeeksAgo(int weeks) => Pick(
            $"{Numerals.Plain(weeks)} هفته پیش",
            weeks == 1 ? "1 week ago" : $"{weeks} weeks ago");

        public static string SelectedCount(int files) => Pick(
            $"{Numerals.Count(files)} فایل انتخاب شده",
            files == 1 ? "1 file selected" : $"{Numerals.Count(files)} files selected");

        public static string MoveToAccount(string account) => Pick(
            $"انتقال به {account}",
            $"Move to {account}");

        public static string CompactNote1 => Pick("فقط ", "Just ");

        public static string CompactNote2 => Pick(
            " روی یک نیای مشترک؛ ردیف‌ها پدینگ را از ",
            " on a shared ancestor; the rows read their padding from ");

        public static string CompactNote3 => Pick(
            " می‌خوانند و از هیچ‌جای دیگر.",
            " and from nowhere else.");

        public static string LoadingState => Pick("حالت بارگذاری", "Loading state");

        public static string SkeletonNoteBefore => Pick(
            "ردیف اسکلتون دقیقاً به ارتفاع ردیف واقعی است — همان ",
            "A skeleton row is exactly as tall as a real one — the same ");

        public static string SkeletonNoteAfter => Pick(
            " به‌علاوه‌ی ارتفاع یک خط متن. جدول موقع رسیدن داده تکان نمی‌خورد.",
            " plus one line of text. The table does not jump when the data arrives.");

        public static string EmptyState => Pick("حالت خالی", "Empty state");

        public static string ProgressBars => Pick("نوارهای پیشرفت", "Progress bars");

        public static string QuotaUnder80 => Pick("آپلود امروز — زیر ۸۰٪", "Today's upload — under 80%");

        public static string QuotaOver80 => Pick("آپلود امروز — از ۸۰٪ گذشته", "Today's upload — past 80%");

        public static string QuotaOver95 => Pick("آپلود امروز — از ۹۵٪ گذشته", "Today's upload — past 95%");

        public static string BarInRow => Pick("ردیفی — ارتفاع ۶", "In a row — 6px tall");

        public static string QuotaRule1 => Pick("قانون سهمیه‌ی روزانه: از ۸۰٪ ", "The daily quota rule: past 80% ");

        public static string QuotaRule2 => Pick("، از ۹۵٪ ", ", past 95% ");

        public static string QuotaRule3 => Pick(
            ". این تصمیم در ",
            ". That decision lives in ");

        public static string QuotaRule4 => Pick(" است، نه در CSS.", ", not in the CSS.");

        public static string FormControls => Pick("کنترل‌های فرم", "Form controls");

        public static string CustomAddress => Pick("آدرس اختصاصی", "Custom address");

        public static string ExpiryDate => Pick("تاریخ انقضا", "Expiry");

        public static string Expiry24Hours => Pick("۲۴ ساعت", "24 hours");

        public static string Expiry14Days => Pick("۱۴ روز", "14 days");

        public static string PasswordProtect => Pick("محافظت با رمز", "Protect with a password");

        public static string PasswordProtectHint => Pick(
            "قبل از دیدن پیش‌نمایش پرسیده می‌شود",
            "Asked for before the preview is shown");

        public static string HideRealName => Pick("پنهان‌کردن نام اصلی فایل", "Hide the real file name");

        public static string HideRealNameHint => Pick(
            "نمایش نام مستعار به گیرنده",
            "The recipient is shown an alias instead");

        public static string DownloadCap => Pick("سقف تعداد دانلود", "Download limit");

        public static string UploadPolicy => Pick("سیاست آپلود", "Upload policy");

        public static string PolicyMostFree => Pick("بیشترین فضای خالی", "Most free space");

        public static string PolicyMostFreeHint => Pick(
            "اکانتی با فضای آزاد بیشتر اول پر می‌شود",
            "The account with more room left is filled first");

        public static string PolicyRoundRobinHint => Pick(
            "پخش متناوب بین اکانت‌ها",
            "Spread turn by turn across the accounts");

        public static string Cards => Pick("کارت‌ها", "Cards");

        public static string CardMain => Pick("کارت اصلی", "Main card");

        public static string CardNote => Pick("شعاع ۱۴ · پدینگ ۱۸ · سایه‌ی ", "Radius 14 · padding 18 · shadow ");

        public static string CardInner => Pick("کارت داخلی", "Inner card");

        public static string TotalSpeed => Pick("سرعت کل", "Total speed");

        public static string AddThirdAccount => Pick(
            "افزودن اکانت سوم — ظرفیت کل به ۱۵TB می‌رسد",
            "Add a third account — total capacity reaches 15TB");

        public static string PreviewPlaceholder => Pick("جای‌نگهدار پیش‌نمایش", "Preview placeholder");

        public static string PdfFirstPage => Pick("پیش‌نمایش صفحه اول PDF", "Preview of the PDF's first page");

        public static string PublicCard => Pick("کارت صفحه‌ی عمومی", "The public page's card");

        public static string SharedFile => Pick("فایل به اشتراک گذاشته‌شده", "Shared file");

        public static string SampleDescription => Pick(
            "گزارش مالی سه‌ماهه سوم ۱۴۰۵ همراه با پیوست‌های تحلیلی. لطفاً پیش از انتشار بیرونی با "
            + "واحد مالی هماهنگ کنید.",
            "The Q3 financial report with its analytical appendices. Please clear it with finance "
            + "before anything goes outside.");

        public static string MetaSize => Pick("حجم", "Size");

        public static string MetaType => Pick("نوع", "Type");

        public static string MetaDate => Pick("تاریخ", "Date");

        public static string MetaExpiry => Pick("اعتبار لینک", "Expires in");

        public static string SampleDate => Pick("۱۴۰۵/۰۵/۳۱", "2026-08-22");

        public static string SampleExpiry => Pick("۱۲ روز", "12 days");

        public static string PublicAssurance => Pick(
            "دانلود مستقیم و استریم‌شده از سرور ما · بدون تبلیغ، بدون انتظار",
            "Streamed directly from our server · no ads, no waiting");

        public static string SampleDownloadCount(long count) => Pick(
            $"{Numerals.Count(count)} بار دانلود شده",
            $"Downloaded {Numerals.Count(count)} times");

        public static string PublicStates => Pick("حالت‌های صفحه‌ی عمومی", "Public page states");

        public static string PublicUnavailableBadge => Pick("غیرقابل دسترس", "Unavailable");

        public static string PublicUnavailableHeading => Pick(
            "این لینک دیگر در دسترس نیست",
            "This link is no longer available");

        public static string PublicUnavailableBody => Pick(
            "ممکن است منقضی شده، به سقف دانلود رسیده یا ابطال شده باشد.",
            "It may have expired, reached its download limit, or been revoked.");

        public static string PublicProtectedBadge => Pick("محافظت‌شده", "Protected");

        public static string PublicProtectedHeading => Pick(
            "برای دیدن فایل رمز را وارد کنید",
            "Enter the password to see this file");

        public static string PublicPasswordLabel => Pick("رمز", "Password");

        public static string PublicContinue => Pick("ادامه", "Continue");
    }

    /// <summary>
    /// What a refused form field says. Reached from the models through
    /// <see cref="LocalizedValidation"/>, because a <c>[Required(ErrorMessage = …)]</c> argument is
    /// a compile-time constant and cannot ask which language the request is in.
    /// </summary>
    public static class Validation
    {
        public static string EmailRequired => Pick("ایمیل را وارد کنید.", "Enter an email address.");

        public static string EmailInvalid => Pick(
            "این نشانی ایمیل معتبر نیست.",
            "That is not a valid email address.");

        public static string PasswordRequired => Pick("گذرواژه را وارد کنید.", "Enter a password.");

        public static string PasswordRepeatRequired => Pick(
            "گذرواژه را دوباره وارد کنید.",
            "Enter the password again.");

        public static string PasswordsDoNotMatch => Pick(
            "دو گذرواژه یکسان نیستند.",
            "The two passwords are not the same.");
    }
}

/// <summary>
/// Marks a catalogue entry whose two renderings are deliberately not two translations — a language
/// naming itself, a byte size, a slug, a domain, a product name that does not get localised.
///
/// It exists for <c>LocalizationCatalogueTests</c>, which otherwise reads "the same string in both
/// cultures" as a translation somebody forgot. The reason is required so the exemption has to be
/// argued rather than reached for.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class VerbatimTextAttribute(string because) : Attribute
{
    public string Because { get; } = because;
}
