namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// «امنیت حساب» — the screen where somebody puts a second lock on their own account, and the two
    /// extra steps the sign-in form grows once they have.
    ///
    /// <para>Its own file rather than more entries under <see cref="Identity"/>, on the rule the
    /// partial exists for: this is one screen and one flow, written at once, and folding it into the
    /// table every other slice is also editing is a merge conflict wearing the costume of tidiness.
    /// The sign-in strings that were already there stay there.</para>
    ///
    /// <para>The register is deliberately plain. Every sentence here is read by somebody who is
    /// either about to make it harder to reach their own films or has just lost the phone that was
    /// holding the key — neither is a moment for the word «authenticator» to appear without being
    /// explained, and neither is a moment for a euphemism. «ورود دومرحله‌ای» and «two-step sign-in»
    /// are what the rest of the world's Persian and English products call this, so they are what
    /// this one calls it too.</para>
    /// </summary>
    public static class Security
    {
        // ---------------------------------------------------------------- the screen itself

        public static string Title => Pick("امنیت حساب", "Account security");

        /// <summary>
        /// The sidebar's name for <c>/security</c>. Shorter than the heading for the reason
        /// <see cref="Shell.OnThisDevice"/> is: the column is 232px and one wrapped row in a menu of
        /// short ones reads as a mistake before it reads as a screen.
        /// </summary>
        public static string NavTitle => Pick("امنیت", "Security");

        public static string Subtitle => Pick(
            "این حساب کلید فایل‌هایتان است. اینجا می‌توانید یک قفل دوم روی آن بگذارید.",
            "This account is the key to your files. Here you can put a second lock on it.");

        public static string TwoStepHeading => Pick("ورود دومرحله‌ای", "Two-step sign-in");

        public static string TwoStepExplanation => Pick(
            "وقتی روشن باشد، دانستن گذرواژه برای ورود کافی نیست: یک کد شش‌رقمی هم لازم است که فقط روی "
            + "گوشی خودتان ساخته می‌شود و هر سی ثانیه عوض می‌شود.",
            "With this on, knowing the password is not enough to sign in: a six-digit code is also "
            + "needed, one that is made only on your own phone and changes every thirty seconds.");

        public static string StateOn => Pick("روشن", "On");

        public static string StateOff => Pick("خاموش", "Off");

        // ---------------------------------------------------------------- turning it on

        public static string TurnOnHeading => Pick(
            "روشن کردن ورود دومرحله‌ای",
            "Turn on two-step sign-in");

        /// <summary>
        /// The instruction that replaces a QR code. See the comment above
        /// <c>SecurityController.SharedKey</c> for why there is no picture here — the short version
        /// is that a hand-drawn one nothing can scan is worse than a key somebody types once.
        /// </summary>
        public static string SetupStepOne => Pick(
            "۱ — یک برنامه‌ی کدساز روی گوشی‌تان نصب کنید. هر کدام که «TOTP» یا «Authenticator» را "
            + "پشتیبانی کند کار می‌کند.",
            "1 — Install a code app on your phone. Any of them that supports «TOTP» or calls itself "
            + "an authenticator will do.");

        public static string SetupStepTwo => Pick(
            "۲ — در آن برنامه «افزودن با کلید» یا «Enter a setup key» را بزنید و این کلید را وارد کنید. "
            + "فاصله‌ها مهم نیستند و حروف کوچک و بزرگ فرقی ندارند.",
            "2 — In that app choose «enter a setup key» and type this key in. The spaces do not "
            + "matter and it is not case-sensitive.");

        public static string SetupStepThree => Pick(
            "۳ — برنامه شروع به ساختن کد می‌کند. کد فعلی را اینجا بنویسید تا روشن شود.",
            "3 — The app starts making codes. Type the current one here to switch it on.");

        public static string SharedKeyLabel => Pick("کلید حساب", "Account key");

        /// <summary>
        /// Beside the key, and it is the whole reason the key is on screen rather than only in a
        /// picture: this string is what somebody needs when the code app refuses the key.
        /// </summary>
        public static string SharedKeyHint => Pick(
            "این کلید فقط تا وقتی روی صفحه است که هنوز روشن نشده‌اید. بعد از روشن شدن دیگر نشان داده "
            + "نمی‌شود؛ اگر گوشی‌تان را عوض کردید، خاموش کنید و دوباره روشن کنید تا کلید تازه بگیرید.",
            "This key is on screen only while this is still off. Once it is on the key is not shown "
            + "again — if you change phones, turn it off and on again for a fresh one.");

        /// <summary>
        /// The <c>otpauth://</c> link. A phone that already has a code app opens it with one press
        /// and the typing disappears; a desktop browser has nothing registered for the scheme and
        /// the press does nothing visible, which is why the key above it is the instruction and this
        /// is the shortcut rather than the other way round.
        /// </summary>
        public static string OpenInCodeApp => Pick(
            "باز کردن در برنامه‌ی کدساز (روی همین گوشی)",
            "Open in a code app on this phone");

        public static string CodeLabel => Pick("کد شش‌رقمی", "Six-digit code");

        public static string TurnOn => Pick("روشن کن", "Turn it on");

        public static string TurnedOn => Pick(
            "ورود دومرحله‌ای روشن شد.",
            "Two-step sign-in is on.");

        // ---------------------------------------------------------------- turning it off

        public static string TurnOffHeading => Pick(
            "خاموش کردن ورود دومرحله‌ای",
            "Turn off two-step sign-in");

        public static string TurnOffExplanation => Pick(
            "برای خاموش کردن هم یک کد لازم است. نشستن پشت یک مرورگر باز کافی نیست — اگر بود، هر کسی که "
            + "به مرورگر شما می‌رسید می‌توانست قفل دوم را بردارد.",
            "Turning it off also takes a code. Sitting at an open browser is not enough — if it were, "
            + "anybody who reached your browser could take the second lock off.");

        public static string TurnOffKeyIsReset => Pick(
            "با خاموش شدن، کلید فعلی هم باطل می‌شود و برنامه‌ی کدساز شما دیگر به درد نمی‌خورد. روشن "
            + "کردن دوباره یعنی یک کلید تازه.",
            "Turning it off also destroys the current key, and your code app's entry stops being "
            + "worth anything. Turning it on again means a fresh key.");

        public static string TurnOff => Pick("خاموش کن", "Turn it off");

        public static string TurnedOff => Pick(
            "ورود دومرحله‌ای خاموش شد.",
            "Two-step sign-in is off.");

        // ---------------------------------------------------------------- recovery codes

        public static string RecoveryHeading => Pick("کدهای پشتیبان", "Recovery codes");

        public static string RecoveryExplanation => Pick(
            "اگر گوشی‌تان گم شد، این‌ها تنها راه ورود شما هستند. هر کدام فقط یک بار کار می‌کند.",
            "If your phone is lost these are your only way in. Each one works exactly once.");

        /// <summary>
        /// The loudest sentence on the screen, and the reason it is loud is the same reason
        /// <see cref="ApiKeys.CopyItNow"/> is: this is the only moment the codes exist outside the
        /// reader's hands. The row keeps a hash of each and nothing else.
        /// </summary>
        public static string RecoveryShownOnce => Pick(
            "این کدها فقط همین یک بار نشان داده می‌شوند. الان جایی بیرون از این مرورگر نگه‌شان دارید — "
            + "چاپ‌شده، یا در یک مدیر گذرواژه. بستن این صفحه یعنی رفتنشان.",
            "These codes are shown this once and never again. Keep them somewhere outside this "
            + "browser now — printed, or in a password manager. Closing this page is losing them.");

        public static string RecoveryRemaining(int codes) => Pick(
            $"{Numerals.Count(codes)} کد استفاده‌نشده باقی مانده است.",
            codes == 1
                ? "1 unused code is left."
                : $"{Numerals.Count(codes)} unused codes are left.");

        public static string RecoveryNoneLeft => Pick(
            "هیچ کد پشتیبانی باقی نمانده است. اگر گوشی‌تان را از دست بدهید راهی برای ورود نخواهید داشت.",
            "No recovery codes are left. Lose your phone now and there is no way back in.");

        public static string RegenerateHeading => Pick("ساخت کدهای پشتیبان تازه", "New recovery codes");

        public static string RegenerateExplanation => Pick(
            "کدهای تازه، کدهای قبلی را باطل می‌کنند — حتی آن‌هایی که هنوز استفاده نشده‌اند. برای این هم "
            + "یک کد لازم است.",
            "A new set kills the old one, including the codes in it you never used. This takes a "
            + "code too.");

        public static string Regenerate => Pick("کدهای تازه بساز", "Make a new set");

        public static string Regenerated => Pick(
            "کدهای پشتیبان تازه ساخته شدند و مجموعه‌ی قبلی باطل شد.",
            "A new set of recovery codes was made, and the old set is dead.");

        // ---------------------------------------------------------------- refusals

        public static string CodeRequired => Pick("کد را بنویسید.", "Type the code.");

        /// <summary>
        /// One sentence for a mistyped code, an expired code and a code from somebody else's phone.
        /// Naming which would be telling a guesser how close they are.
        /// </summary>
        public static string BadCode => Pick(
            "این کد درست نیست. کد فعلی برنامه را بنویسید؛ هر کد فقط حدود یک دقیقه معتبر است.",
            "That code is not right. Use the current one from the app — each is good for about a "
            + "minute.");

        public static string BadRecoveryCode => Pick(
            "این کد پشتیبان درست نیست، یا قبلاً استفاده شده است.",
            "That recovery code is not right, or it has already been used.");

        // What a run of wrong codes on this screen earns is Identity.LockedOut — the same sentence a
        // run of wrong passwords earns, because it is the same counter and the same lock. A second
        // entry saying it in slightly different words would be two sentences for one fact, and the
        // one a reader gets would depend on which form they were standing at when it tripped.

        public static string AlreadyOn => Pick(
            "ورود دومرحله‌ای از قبل روشن است.",
            "Two-step sign-in is already on.");

        public static string AlreadyOff => Pick(
            "ورود دومرحله‌ای از قبل خاموش است.",
            "Two-step sign-in is already off.");

        // ---------------------------------------------------------------- who ought to have it

        /// <summary>
        /// Said to operator staff and to nobody else. It is not a gate — see the comment on
        /// <c>SecurityController</c> for why a deployment that forces this on its only operator can
        /// lock its owner out of their own product with no way back.
        /// </summary>
        public static string OperatorShould => Pick(
            "حساب شما اپراتور است: گذرواژه‌ی همین حساب، کلید فایل‌های همه‌ی فضاهای کاری و همه‌ی "
            + "اکانت‌های گوگل است. اگر قرار است فقط یک حساب در این نصب ورود دومرحله‌ای داشته باشد، "
            + "همین یکی است.",
            "Yours is an operator account: this one password is the key to every workspace's files "
            + "and to every Google account. If exactly one account in this deployment is going to "
            + "have two-step sign-in, it is this one.");

        /// <summary>
        /// What turning it on or off does to the other places this account is signed in. Said on the
        /// screen rather than discovered on a phone that has stopped working.
        /// </summary>
        public static string OtherSessionsEnd => Pick(
            "با هر بار روشن یا خاموش کردن، همه‌ی دستگاه‌های دیگری که با این حساب وارد شده‌اند بیرون "
            + "می‌آیند. همین مرورگر باز می‌ماند.",
            "Turning this on or off signs this account out everywhere else. This browser stays.");

        // ---------------------------------------------------------------- the second step at sign-in

        public static string ChallengeTitle => Pick("مرحله‌ی دوم", "Second step");

        public static string ChallengeHeading => Pick("کد گوشی‌تان را بنویسید", "Type the code from your phone");

        public static string ChallengeSubtitle => Pick(
            "گذرواژه درست بود. برای این حساب یک کد هم لازم است.",
            "The password was right. This account needs a code as well.");

        public static string SignInWithCode => Pick("ورود", "Sign in");

        public static string LostYourPhone => Pick("گوشی‌تان را ندارید؟", "Do not have your phone?");

        public static string UseRecoveryCode => Pick("ورود با کد پشتیبان", "Sign in with a recovery code");

        public static string RecoveryTitle => Pick("کد پشتیبان", "Recovery code");

        public static string RecoveryChallengeHeading => Pick(
            "یکی از کدهای پشتیبانتان را بنویسید",
            "Type one of your recovery codes");

        public static string RecoveryChallengeSubtitle => Pick(
            "همانی که موقع روشن کردن ورود دومرحله‌ای نگه داشتید. با استفاده، همان یکی مصرف می‌شود و "
            + "بقیه سر جایشان می‌مانند.",
            "One of the ones you kept when you turned this on. Using it spends that one code and "
            + "leaves the rest.");

        public static string BackToCode => Pick("گوشی‌تان را دارید؟", "Have your phone after all?");

        public static string UseCodeInstead => Pick("ورود با کد گوشی", "Sign in with the app's code");

        /// <summary>
        /// What a sign-in that spent a recovery code says next. Sent to the security screen rather
        /// than to the dashboard, because the reader has one fewer way back in than they had this
        /// morning and this is the only screen that can give it back.
        /// </summary>
        public static string RecoveryCodeSpent => Pick(
            "با یک کد پشتیبان وارد شدید و آن کد مصرف شد. اگر گوشی‌تان را از دست داده‌اید، همین حالا "
            + "ورود دومرحله‌ای را خاموش و دوباره روشن کنید تا با گوشی تازه تنظیم شود.",
            "You signed in with a recovery code and that code is now spent. If your phone is gone, "
            + "turn two-step sign-in off and on again now to set it up on the new one.");

        /// <summary>
        /// The refusal when the half-finished sign-in has expired between the two steps. It says the
        /// password again rather than «session expired», because what the reader has to do is start
        /// over at the form and nothing else.
        /// </summary>
        public static string ChallengeExpired => Pick(
            "این مرحله منقضی شد. از اول با ایمیل و گذرواژه وارد شوید.",
            "That step expired. Start again with your email address and password.");
    }
}
