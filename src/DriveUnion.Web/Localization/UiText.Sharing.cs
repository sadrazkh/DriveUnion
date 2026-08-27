namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// The words on the control that hands a finished share link to the phone's own share sheet.
    ///
    /// <para>Its own section rather than more of <see cref="Files"/>, and its own file, because the
    /// sentence below is not a label on the files screen — it is a message the owner sends to
    /// somebody who has never seen this product. It is written for the recipient, and the screen it
    /// is pressed on is incidental to it.</para>
    /// </summary>
    public static class Sharing
    {
        /// <summary>
        /// The button, beside «کپی» and never instead of it.
        ///
        /// <para>«هم‌رسانی» rather than «اشتراک‌گذاری»: it is the word iOS and Android already use
        /// on the sheet this button opens, and the panel says «لینک اشتراک» for the thing being
        /// sent — one word for the object and another for the act keeps the two apart in a row
        /// where both appear.</para>
        /// </summary>
        public static string Share => Pick("هم‌رسانی", "Share");

        /// <summary>
        /// What the recipient reads first, above the link.
        ///
        /// <para>Almost every share target concatenates <c>text</c> and <c>url</c>, so this is the
        /// whole of the message somebody receives — and a bare <c>/d/kx91mzq4</c> arriving with no
        /// sentence in front of it is indistinguishable from the links people are told not to open.
        /// The file's name is in it for the same reason: it is what makes the address mean
        /// something, and it discloses nothing the landing page behind the link does not already
        /// show to whoever opens it.</para>
        ///
        /// <para>The name goes through <see cref="Ltr"/>. A file name is a Latin run inside a
        /// Persian sentence — the same shape as every byte size in this panel — and without the
        /// isolate the bidirectional algorithm resolves the spaces around it to the paragraph's
        /// direction and lays «Q3-Report-Final.pdf» out among the Persian words in the wrong place.
        /// This string is not rendered by this product at all, which is exactly why the isolate has
        /// to travel inside it: there is no element around it in the recipient's messaging app to
        /// carry a <c>dir</c>.</para>
        /// </summary>
        public static string ShareMessage(string fileName) => Pick(
            $"فایل «{Ltr(fileName)}» برای شما",
            $"«{fileName}» — a file for you");

        /// <summary>
        /// Said on the button when the sheet would not open, and left standing there.
        ///
        /// <para>Not shown when the sender dismisses the sheet — that is the control working. This
        /// is the other rejection: no user activation left by the time the call was made, a
        /// permissions policy, a target that threw. It stays on the button rather than timing out,
        /// the way the redirect-URI copy control answers a refused clipboard, because the sentence
        /// is an instruction and the thing it points at is the button next to it.</para>
        /// </summary>
        public static string ShareRefused => Pick(
            "باز نشد — نشانی را کپی کنید",
            "Would not open — copy the address");
    }
}
