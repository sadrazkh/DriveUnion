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

/// <summary>
/// The second step of a sign-in: the password was right and the account wants a code as well.
///
/// <para>Nothing here identifies the account. Who is half-way through signing in is held by
/// Identity's own <c>TwoFactorUserId</c> cookie, written by <c>PasswordSignInAsync</c> and read by
/// <c>TwoFactorAuthenticatorSignInAsync</c> — a user id in this form would let a caller who never
/// produced a password nominate whose second factor they are answering.</para>
/// </summary>
public sealed class TwoFactorViewModel
{
    /// <summary>
    /// Six digits from the code app, or the same with a space or a dash in it — the controller
    /// strips both before Identity parses it as a number.
    ///
    /// <para>No <c>[LocalizedRequired]</c>, and the omission is deliberate rather than an oversight:
    /// «you typed nothing» and «that code is wrong» are the same refusal to whoever is standing at
    /// this form, and splitting them into a field error and a form error would put two different
    /// sentences in two different places for one mistake. The controller answers both with the one
    /// line, and — the part an attribute could not do — counts the empty attempt against nothing,
    /// because there was no guess in it.</para>
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Carried across from the first step, because the checkbox that answered it is two pages back.
    ///
    /// <para>Without this the answer somebody gave on the sign-in form would be silently discarded
    /// at the step that finally issues the cookie, and every account with two-step sign-in on would
    /// get a session cookie — the phone-eviction bug that <c>LoginViewModel.RememberMe</c> exists to
    /// fix, reintroduced for exactly the accounts that took security most seriously.</para>
    /// </summary>
    public bool RememberMe { get; set; }

    /// <summary>
    /// Where the visitor was going before the panel asked them who they were. Carried across the
    /// second step for the same reason as the checkbox: it was answered two pages back, and losing
    /// it here would land every account with two-step sign-in on the dashboard instead of on the
    /// file they followed a link to.
    /// </summary>
    public string? ReturnUrl { get; set; }
}

/// <summary>
/// The way in for somebody whose phone is gone. Same step, different credential.
/// </summary>
public sealed class RecoveryCodeViewModel
{
    /// <summary>One of the ten. See <see cref="TwoFactorViewModel.Code"/> for why it carries no attribute.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Carried so the two forms are interchangeable: somebody who starts at the app's code, gives up
    /// and crosses to this one must not silently lose either answer they already gave.
    /// </summary>
    public bool RememberMe { get; set; }

    /// <inheritdoc cref="TwoFactorViewModel.ReturnUrl"/>
    public string? ReturnUrl { get; set; }
}
