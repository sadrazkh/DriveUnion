namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The link-fetch screen's words that changed when its key protocol did.
    ///
    /// <para>In a partial of its own because the entries beside it in <c>UiText.cs</c> are about the
    /// feature and these are about one property of it — and because this is the file somebody will
    /// find when they wonder why a passphrase box disappeared from a form.</para>
    /// </summary>
    public static class Fetching
    {
        /// <summary>
        /// Why there is no passphrase box on the no-script form.
        ///
        /// <para>It says what will happen rather than apologising for what will not. Somebody
        /// reading this has JavaScript switched off deliberately and is owed the fact — the file
        /// arrives readable — rather than an explanation of a protocol change they did not ask
        /// about.</para>
        ///
        /// <para>The alternative was leaving the box and sending the passphrase to the server. That
        /// would have been worse than removing it: the customer would believe they had locked
        /// something, and the whole reason the box is gone is that the server should not be given
        /// the one secret that also opens everything they locked in their browser.</para>
        /// </summary>
        public static string NoLockWithoutScript => Pick(
            "بدون جاوااسکریپت فایل همان‌طور که هست ذخیره می‌شود. قفل‌کردن هنگام دریافت، به مرورگر نیاز دارد — چون کلید در مرورگر شما ساخته می‌شود و رمزتان اصلاً به سرور نمی‌رسد.",
            "Without JavaScript the file is stored as it comes. Locking it on the way in needs the "
            + "browser, because the key is made there and your passphrase never reaches the server.");
    }
}
