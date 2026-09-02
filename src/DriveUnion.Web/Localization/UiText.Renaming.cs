namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// Giving a file a different name.
    ///
    /// <para>Its own partial rather than lines in <c>UiText.cs</c>, for the reason that class is
    /// partial: the main table had become the one place several unrelated pieces of work all had to
    /// edit at once.</para>
    /// </summary>
    public static class Renaming
    {
        public static string Heading => Pick("تغییر نام", "Rename");

        public static string Save => Pick("ثبت نام تازه", "Save the new name");

        public static string Done => Pick("نام عوض شد.", "The name was changed.");

        /// <summary>
        /// Refused for having nothing in it once the unusable characters were taken out.
        ///
        /// <para>Says what was removed rather than «invalid», because a name of «../» is refused for
        /// a reason the person typing it cannot otherwise guess — and because the usual case is a
        /// paste that carried a path, where the useful thing to know is that the path part goes.</para>
        /// </summary>
        public static string NothingLeft => Pick(
            "این نام چیزی برای نگه داشتن ندارد. جداکننده‌های مسیر برداشته می‌شوند، پس نامی بنویسید که بدون آن‌ها هم نامی باشد.",
            "There is nothing left of that name. Path separators are removed, so write one that is "
            + "still a name without them.");
    }
}
