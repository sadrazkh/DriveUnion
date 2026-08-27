namespace DriveUnion.Web.Localization;

public static partial class UiText
{
    /// <summary>
    /// What the sign-in form says about staying signed in.
    ///
    /// <para>Its own section rather than more entries under <see cref="Identity"/>, because it is
    /// about one control on one form and it has one job: an authentication cookie is now left on
    /// the machine in front of the visitor for thirty days by default, and the only moment that
    /// fact is useful to them is the moment they are typing a password into somebody else's
    /// computer. A help page nobody opens does not say it; the line under the checkbox does.</para>
    ///
    /// <para>The number is a parameter and never a literal. It comes from the cookie handler's own
    /// <c>ExpireTimeSpan</c> by way of <c>LoginViewModel.StaySignedInDays</c>, so the sentence
    /// cannot drift away from the credential it describes — a form that promises thirty days while
    /// the deployment grants seven is worse than a form that says nothing.</para>
    /// </summary>
    public static class SignIn
    {
        /// <param name="days">
        /// The cookie's own life in days, rounded. Persian numerals in Persian prose and Latin in
        /// English, which is the panel's rule for a figure set in a sentence rather than in a
        /// technical readout.
        /// </param>
        public static string StaySignedInHint(int days) => Pick(
            $"تا {Numerals.Plain(days)} روز دیگر گذرواژه خواسته نمی‌شود. روی رایانه‌ای که با کس دیگری "
            + "مشترک است این را خاموش کنید؛ آن‌وقت با بستن مرورگر از حساب بیرون می‌آیید.",
            days == 1
                ? "The panel will not ask for your password again for a day. Turn this off on a "
                  + "computer you share with anybody — then closing the browser signs you out."
                : $"The panel will not ask for your password again for {days} days. Turn this off "
                  + "on a computer you share with anybody — then closing the browser signs you out.");
    }
}
