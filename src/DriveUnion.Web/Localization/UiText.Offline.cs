namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The one page the service worker keeps on the device.
    ///
    /// <para>Its own file rather than lines in <c>UiText.cs</c>, for the reason that class is
    /// partial: the main table had become the one place several unrelated pieces of work all had to
    /// edit at once.</para>
    ///
    /// <para>These words are read in a situation no other screen is written for. There is no
    /// network, so nothing on the page can be fetched and no link on it can be relied on to arrive
    /// anywhere; and the reader has just pressed something that did not work, so the first thing
    /// they need is to be told that the panel is not broken. Short sentences, no jargon about
    /// caches or workers, and nothing that promises what the next tap will do.</para>
    /// </summary>
    public static class Offline
    {
        /// <summary>The document title, and the heading — one word for one fact.</summary>
        public static string Title => Pick("آفلاین", "Offline");

        /// <summary>
        /// What happened, said as a fact about the connection rather than about the app.
        ///
        /// <para>"The page did not load" and not "something went wrong": somebody whose phone has
        /// lost signal already knows what to do about it, and the only thing this page can add is
        /// certainty about which half failed.</para>
        /// </summary>
        public static string Body => Pick(
            "این دستگاه به اینترنت وصل نیست، برای همین صفحه‌ای که خواستید باز نشد.",
            "This device has no connection, so the page you asked for did not load.");

        /// <summary>
        /// Why there is no file list on this screen either — which is the product's own claim,
        /// restated at the one moment a customer would otherwise read it as a fault.
        ///
        /// <para>An installed app that shows nothing offline looks unfinished unless somebody says
        /// why. The reason is the reason the product exists: names and files are not written to this
        /// phone, so there is nothing here to show without asking the server.</para>
        /// </summary>
        public static string NothingStored => Pick(
            "فایل‌ها و نام‌هایشان روی این دستگاه ذخیره نمی‌شوند، پس دیدنشان به اینترنت نیاز دارد.",
            "Files and their names are not kept on this device, so seeing them needs a connection.");

        /// <summary>
        /// The one control, and it is a link rather than a button on purpose.
        ///
        /// <para>A button that reloads needs JavaScript to mean anything, and the panel's rule is
        /// that nothing in a view may be written so that it only makes sense while a script is
        /// running. A link to the panel's front door works with the bundle or without it: when the
        /// connection is back it opens the panel, and when it is not it lands here again, which is
        /// the truthful answer rather than a spinner.</para>
        /// </summary>
        public static string Retry => Pick("تلاش دوباره", "Try again");
    }
}
