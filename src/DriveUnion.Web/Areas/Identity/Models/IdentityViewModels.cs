using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DriveUnion.Web.Areas.Identity.Models;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "ایمیل را وارد کنید.")]
    [EmailAddress(ErrorMessage = "این نشانی ایمیل معتبر نیست.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "گذرواژه را وارد کنید.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
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
    [Required(ErrorMessage = "ایمیل را وارد کنید.")]
    [EmailAddress(ErrorMessage = "این نشانی ایمیل معتبر نیست.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "گذرواژه را وارد کنید.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The second box. There is no password reset in M1 and no mail sender behind one, so a typo
    /// here locks the owner out of their own panel with no way back except the database.
    /// </summary>
    [Required(ErrorMessage = "گذرواژه را دوباره وارد کنید.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "دو گذرواژه یکسان نیستند.")]
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
