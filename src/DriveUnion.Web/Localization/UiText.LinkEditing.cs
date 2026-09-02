namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// Changing a link that has already been handed out.
    ///
    /// <para>Its own partial rather than lines in <c>UiText.cs</c>, for the reason that class is
    /// partial: the main table had become the one place several unrelated pieces of work all had to
    /// edit at once.</para>
    /// </summary>
    public static class LinkEditing
    {
        public static string Heading => Pick("تغییر این لینک", "Change this link");

        public static string ExpiryLabel => Pick("تاریخ انقضا", "Expires on");

        /// <summary>Empty means no expiry, which is a link with nothing stopping it.</summary>
        public static string ExpiryNone => Pick("خالی یعنی بدون انقضا", "Empty means no expiry");

        public static string CapLabel => Pick("سقف دانلود", "Download limit");

        public static string CapNone => Pick("خالی یعنی بی‌نهایت", "Empty means no limit");

        public static string NoteLabel => Pick("یادداشت برای گیرنده", "A note for the recipient");

        public static string Save => Pick("ثبت تغییر", "Save the change");

        public static string Done => Pick("لینک تغییر کرد.", "The link was changed.");

        /// <summary>
        /// Refused because the new ceiling is under what the link has already been used for.
        ///
        /// <para>Both figures, because the reader's next move depends on the gap: a link on 7 of 10
        /// being set to 8 is a typo, and being set to 3 is somebody who meant to revoke it. Neither
        /// is served by «invalid».</para>
        /// </summary>
        public static string BelowSpent => Pick(
            "این سقف از تعداد دانلودهایی که تا حالا انجام شده کمتر است.",
            "That limit is below the number of downloads already taken.");

        /// <summary>
        /// Said where the two figures go, and the reason revoking is not reachable from this form.
        ///
        /// <para>Revoking burns a slug for ever, so an edit that could undo it would be an undo for
        /// the one action in this product that has none. The button for it is the one beside this
        /// form, and it stays a different button.</para>
        /// </summary>
        public static string RevokeIsElsewhere => Pick(
            "برای باطل کردن، دکمهٔ جداگانه‌اش را بزنید. باطل کردن برگشت‌پذیر نیست.",
            "To revoke it, use the separate button. Revoking cannot be undone.");
    }
}
