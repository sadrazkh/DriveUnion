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
