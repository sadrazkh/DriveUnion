using System.ComponentModel.DataAnnotations;

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
/// Three different refusals wear the same 403, and telling them apart is the whole value of this
/// page: a customer with no workspace is waiting on somebody, a customer on an operator screen is
/// not going to be let in, and an operator on a customer screen is simply in the wrong half of the
/// panel.
/// </summary>
/// <param name="HasWorkspace">The caller has a usable tenant claim.</param>
/// <param name="IsOperator">The caller is operator staff.</param>
public sealed record AccessDeniedViewModel(bool HasWorkspace, bool IsOperator);
