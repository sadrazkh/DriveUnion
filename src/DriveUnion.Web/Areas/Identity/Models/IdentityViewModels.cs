using System.ComponentModel.DataAnnotations;
using DriveUnion.Web.Localization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DriveUnion.Web.Areas.Identity.Models;

/// <summary>
/// The refusals name a catalogue entry rather than carrying a sentence, because an attribute
/// argument is a compile-time constant and cannot know which language the request is in. See
/// <see cref="LocalizedRequiredAttribute"/>.
/// </summary>
public sealed class LoginViewModel
{
    [LocalizedRequired(ValidationText.EmailRequired)]
    [LocalizedEmailAddress(ValidationText.EmailInvalid)]
    public string Email { get; set; } = string.Empty;

    [LocalizedRequired(ValidationText.PasswordRequired)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Ticked when the form is drawn, which is the half of "stay signed in" that a phone can see.
    ///
    /// <para>Program.cs gives the cookie an explicit thirty-day sliding life, and that bounds the
    /// ticket whatever this says. What this decides is whether the browser is handed an expiry at
    /// all: a sign-in that is not persistent produces a session cookie, an installed iOS web app
    /// loses its session every time the system evicts it from memory, and the panel then asks for a
    /// password on nearly every cold open. Defaulting to <c>false</c> was that bug.</para>
    ///
    /// <para>Defaulted rather than forced. The box is still there, unticking it still produces a
    /// session cookie, and the form says why somebody on a shared computer would want that — see
    /// the hidden companion field in Login.cshtml, without which unticking it would post nothing
    /// and this initialiser would silently win.</para>
    /// </summary>
    public bool RememberMe { get; set; } = true;

    /// <summary>
    /// How long ticking the box actually keeps somebody signed in, so the form can say so.
    ///
    /// <para><see cref="BindNeverAttribute"/> and filled by the controller from the cookie
    /// handler's own <c>ExpireTimeSpan</c>, on the same rule as the setup screen's password
    /// rules: a sentence here that transcribed «thirty» would go on saying thirty the day a
    /// deployment sets seven, and the one thing this line exists to do is tell the truth about how
    /// long a credential is being left on the machine in front of them.</para>
    /// </summary>
    [BindNever]
    public int StaySignedInDays { get; set; }
}

/// <summary>
/// The first-run screen: an empty panel asking for the operator it does not have.
///
/// The three requirement fields are <see cref="BindNeverAttribute"/> on purpose. They decide what
/// the page tells the visitor about the password policy and whether it offers to invent one, and
/// this form is posted by an anonymous caller — a request that could set them would be choosing its
/// own copy on the one screen in the product that mints an administrator. The controller fills them
/// from <c>IdentityOptions</c> and the hosting environment on every render.
/// </summary>
public sealed class SetupViewModel
{
    [LocalizedRequired(ValidationText.EmailRequired)]
    [LocalizedEmailAddress(ValidationText.EmailInvalid)]
    public string Email { get; set; } = string.Empty;

    [LocalizedRequired(ValidationText.PasswordRequired)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The second box. There is no password reset in M1 and no mail sender behind one, so a typo
    /// here locks the owner out of their own panel with no way back except the database.
    /// </summary>
    [LocalizedRequired(ValidationText.PasswordRepeatRequired)]
    [DataType(DataType.Password)]
    [LocalizedCompare(nameof(Password), ValidationText.PasswordsDoNotMatch)]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// The password policy in words, read from Identity's own options rather than written out here.
    /// <c>RequiredLength = 10</c> lives in Program.cs; a sentence in this project repeating it would
    /// go on saying ten the day somebody sets twelve.
    /// </summary>
    [BindNever]
    public IReadOnlyList<string> PasswordRules { get; set; } = [];

    /// <summary>Development only. See the view for why nothing is generated anywhere else.</summary>
    [BindNever]
    public bool OfferGeneratedPassword { get; set; }

    /// <summary>What a suggested password has to clear, so the browser can generate one that does.</summary>
    [BindNever]
    public int MinimumPasswordLength { get; set; }
}

/// <summary>
/// Three different refusals wear the same 403, and telling them apart is the whole value of this
/// page: a customer with no workspace is waiting on somebody, a customer on an operator screen is
/// not going to be let in, and an operator on a customer screen is simply in the wrong half of the
/// panel.
/// </summary>
/// <param name="HasWorkspace">The caller has a usable tenant claim.</param>
/// <param name="IsOperator">The caller is operator staff.</param>
public sealed record AccessDeniedViewModel(bool HasWorkspace, bool IsOperator);
