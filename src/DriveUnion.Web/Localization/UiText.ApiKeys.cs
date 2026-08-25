namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>«کلیدهای API» — the screen where a customer mints the key their program uses.</summary>
    public static class ApiKeys
    {
        public static string Title => Pick("کلیدهای API", "API keys");

        public static string Subtitle => Pick(
            "برای وصل کردن برنامه‌ی خودتان به فایل‌هایتان، بدون اینکه رمز عبورتان جایی برود.",
            "For wiring your own program to your files, without your password going anywhere.");

        public static string Count(int keys) => Pick(
            $"{Numerals.Count(keys)} کلید",
            keys == 1 ? "1 key" : $"{Numerals.Count(keys)} keys");

        public static string NewKey => Pick("کلید جدید", "New key");

        public static string KeyName => Pick("نام کلید", "Key name");

        public static string KeyNameHint => Pick(
            "جایی که استفاده می‌شود، مثلاً «سرور پشتیبان‌گیری».",
            "Where it will be used — «backup server», say.");

        public static string Scope => Pick("دسترسی", "Access");

        public static string ScopeRead => Pick("فقط خواندن", "Read only");

        public static string ScopeWrite => Pick("خواندن و نوشتن", "Read and write");

        public static string ScopeHint => Pick(
            "کلید فقط‌خواندنی نه چیزی آپلود می‌کند نه چیزی پاک. اگر شک دارید همین را بگیرید.",
            "A read-only key uploads nothing and deletes nothing. Take that one if you are unsure.");

        public static string ExpiresIn => Pick("انقضا (روز)", "Expires in (days)");

        public static string ExpiresHint => Pick(
            "خالی بگذارید تا منقضی نشود. کلیدی که تاریخ ندارد همانی است که یادتان می‌رود صادرش کرده‌اید.",
            "Leave it empty for never. A key with no end is the one you forget you issued.");

        // ---------------------------------------------------------------- the one-time secret

        /// <summary>
        /// The heading over the secret, and it has one job: make somebody copy it now.
        ///
        /// <para>Nothing in this product can produce it again — the row holds a SHA-256 of it and
        /// nothing else — so a customer who closes this page has a key they can only revoke.</para>
        /// </summary>
        public static string CopyItNow => Pick(
            "همین حالا کپی‌اش کنید",
            "Copy it now");

        public static string CopyItNowBody => Pick(
            "این تنها باری است که این کلید نشان داده می‌شود. ما فقط اثر انگشتش را نگه می‌داریم، نه خودش را — اگر این صفحه را ببندید دیگر نمی‌شود بازیابی‌اش کرد، فقط می‌شود باطلش کرد و یکی دیگر ساخت.",
            "This is the only time this key is shown. We keep a fingerprint of it and not the key itself — close this page and it cannot be recovered, only revoked and replaced.");

        public static string Copy => Pick("کپی", "Copy");

        // ---------------------------------------------------------------- the table

        public static string ColumnName => Pick("نام", "Name");

        public static string ColumnKey => Pick("کلید", "Key");

        public static string ColumnScope => Pick("دسترسی", "Access");

        public static string ColumnLastUsed => Pick("آخرین استفاده", "Last used");

        public static string ColumnState => Pick("وضعیت", "State");

        public static string NeverUsed => Pick("هرگز", "Never");

        public static string StateLive => Pick("فعال", "Live");

        public static string StateRevoked => Pick("باطل شده", "Revoked");

        public static string StateExpired => Pick("منقضی شده", "Expired");

        public static string StateExpiresOn(string when) => Pick($"تا {Ltr(when)}", $"Until {when}");

        public static string Revoke => Pick("ابطال", "Revoke");

        public static string EmptyHeading => Pick(
            "هنوز کلیدی نساخته‌اید.",
            "You have not made a key yet.");

        // ---------------------------------------------------------------- outcomes

        public static string Minted => Pick("کلید ساخته شد.", "The key was made.");

        public static string Revoked => Pick(
            "کلید باطل شد. هر برنامه‌ای که از آن استفاده می‌کرد از همین حالا رد می‌شود.",
            "The key is revoked. Anything using it is refused from now on.");

        public static string NeedsAName => Pick(
            "کلید بدون نام نمی‌شود؛ بعداً خودتان باید بفهمید کدام کدام است.",
            "A key needs a name; later you will be the one telling them apart.");

        public static string TooMany(int limit) => Pick(
            $"بیشتر از {Numerals.Count(limit)} کلید فعال نمی‌شود. یکی را باطل کنید.",
            $"There is room for {Numerals.Count(limit)} live keys. Revoke one first.");

        // ---------------------------------------------------------------- using it

        public static string HowToHeading => Pick("چطور استفاده کنید", "How to use it");

        /// <summary>
        /// The whole of the protocol, in one line somebody can paste.
        ///
        /// <para>An example rather than prose: «send it as a bearer token» is a sentence somebody
        /// has to translate into a command, and the translation is where the mistakes are.</para>
        /// </summary>
        [VerbatimText("a curl invocation is a command, not a sentence")]
        public static string HowToExample(string url) =>
            $"curl -H \"Authorization: Bearer du_…\" {url}";

        public static string HowToBody => Pick(
            "هر درخواست کلید را در سرآیند Authorization می‌فرستد. کلید مثل رمز عبور است: در مخزن کد نگذاریدش.",
            "Every request sends the key in the Authorization header. Treat it like a password: keep it out of your repository.");
    }
}
