namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The words a phone shows when this is installed to a home screen.
    ///
    /// <para>They are read in two places nothing else in this catalogue is read: under an icon on a
    /// home screen, and in the operating system's own install sheet. Both are short and neither
    /// wraps kindly, so the constraint here is width rather than tone.</para>
    /// </summary>
    public static class Pwa
    {
        /// <summary>
        /// The name in the install sheet, where there is room for it.
        ///
        /// <para>The same string the panel already calls itself — see <see cref="Brand.Name"/>. It is
        /// spelled through that property rather than repeated, because a product renamed in one place
        /// and not the other is a home screen icon that disagrees with the page it opens.</para>
        /// </summary>
        public static string Name => Brand.Name;

        /// <summary>
        /// The name under the icon, where there is not.
        ///
        /// <para>iOS truncates at roughly twelve characters and then draws an ellipsis; «درایو
        /// یونیون» is thirteen with the space and would land as «درایو یونیو…». One word instead, in
        /// both languages, and each is the half a speaker of that language would keep.</para>
        /// </summary>
        public static string ShortName => Pick("درایو", "Drive");

        /// <summary>
        /// What it is, for the install sheet and for a store listing that scrapes one.
        ///
        /// <para>It says what the customer gets and not how it is built. The pool of Google accounts
        /// underneath is the operator's business and appears nowhere a customer can read — the same
        /// rule the whole panel keeps.</para>
        /// </summary>
        public static string Description => Pick(
            "فایل‌هایتان را آپلود کنید، لینک بدهید، و هر جا لازم شد به آن‌ها برسید.",
            "Upload your files, share a link, and reach them from anywhere.");
    }
}
