namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The QR beside a share link.
    ///
    /// <para>Its own partial rather than lines in <c>UiText.cs</c>, for the reason that class is
    /// partial: the main table had become the one place several unrelated pieces of work all had to
    /// edit at once.</para>
    /// </summary>
    public static class LinkQr
    {
        /// <summary>
        /// The disclosure that reveals it, worded as what it is for rather than as what it is.
        ///
        /// <para>«QR code» names the picture; «scan on a phone» names the reason somebody would open
        /// a disclosure to look at one. The picture is recognisable enough not to need labelling.</para>
        /// </summary>
        public static string Show => Pick("اسکن با گوشی", "Scan on a phone");

        /// <summary>
        /// What a screen reader is told the picture is.
        ///
        /// <para><b>Deliberately not the address.</b> A title element is text in the markup, and the
        /// whole point of the exercise is that the slug exists only in the modules — see
        /// <c>LinkQrCode.Svg</c>, which refuses to put it there for the same reason. The address is
        /// already on the line above in a field anyone can read and copy.</para>
        /// </summary>
        public static string PictureLabel => Pick(
            "کد QR این لینک",
            "A QR code for this link");
    }
}
