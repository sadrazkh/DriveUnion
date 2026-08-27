namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// What a transfer can and cannot survive, said on the screen where somebody starts one.
    ///
    /// <para>Its own partial file for the ordinary reason — <c>UiText.cs</c> is the one table three
    /// unrelated pieces of work all edit at once — and its own section because these sentences are
    /// about the mechanics of moving bytes rather than about the upload screen's controls, which
    /// live in <c>UiText.Files</c>.</para>
    ///
    /// <para>The upload screen's other words are still literals inside
    /// <c>Scripts/islands/UploadPanel.vue</c>, and that is not an oversight this file quietly
    /// contradicts. The island's dictionaries are reachable from Razor only through a
    /// <c>data-*</c> attribute on the mount point, and the dock's mount point is in
    /// <c>_Layout.cshtml</c>; a status word has to be in both views or in neither, so the two views
    /// keep their pair of dictionaries and the prose that belongs to the *screen* comes from
    /// here.</para>
    /// </summary>
    public static class Transfers
    {
        /// <summary>
        /// The sentence that stops this being reported as a bug.
        ///
        /// <para>iOS suspends a web app the moment it is backgrounded and WebKit has no Background
        /// Fetch, so a transfer genuinely stops when somebody switches apps or the screen locks.
        /// Nothing in this product can change that. What it can do is pick the transfer up again on
        /// the way back in — which it now does, without being asked — and say so before a customer
        /// discovers the first half on their own and concludes the upload is broken.</para>
        ///
        /// <para>It claims exactly what is true and not a word more. «Continues when you come back»
        /// is a promise that is kept; «uploads in the background» is one that cannot be, on the
        /// phone this sentence exists for.</para>
        /// </summary>
        public static string LeavingTheAppPauses => Pick(
            "فرستادن فایل از این دستگاه تا وقتی ادامه دارد که برنامه جلوی چشمتان باشد: رفتن به "
            + "برنامه‌ای دیگر یا قفل شدن صفحه، آپلود را متوقف می‌کند، و وقتی برگردید از همان‌جا که "
            + "مانده بود خودش ادامه پیدا می‌کند.",
            "Sending a file from this device needs the app in front of you: switching to another app "
            + "or locking the screen stops the transfer, and it carries on by itself from where it "
            + "stopped when you come back.");
    }
}
