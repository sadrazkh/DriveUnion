using System.ComponentModel.DataAnnotations;

namespace DriveUnion.Web.Localization;

/// <summary>
/// The refusals a form field can carry, named so a model can point at one.
///
/// An enum and not a string key: <c>[Required(ErrorMessage = "…")]</c> takes a compile-time
/// constant, so the message cannot be the sentence itself — but it can be the <em>name</em> of one,
/// and a name the compiler checks is the whole reason this catalogue is C# rather than a resource
/// file. A missing case here is a build error; a mistyped resource key would be a customer reading
/// <c>Validation.EmailRequired</c> on a sign-in form.
/// </summary>
public enum ValidationText
{
    EmailRequired,
    EmailInvalid,
    PasswordRequired,
    PasswordRepeatRequired,
    PasswordsDoNotMatch,
}

/// <summary>
/// DataAnnotations attributes that ask <see cref="UiText"/> for their message when the field is
/// refused, rather than carrying an already-chosen sentence from compile time.
///
/// Validation runs inside the request, after the localisation middleware has settled
/// <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>, so
/// <see cref="ValidationAttribute.FormatErrorMessage"/> is the right and only hook: it is called
/// once per refusal rather than once per application start.
///
/// The framework's answer to the same problem is <c>ErrorMessageResourceType</c> and
/// <c>AddDataAnnotationsLocalization</c>, both of which need <c>.resx</c>. See <see cref="UiText"/>
/// for why this codebase does not have any.
/// </summary>
internal static class LocalizedValidation
{
    internal static string Message(ValidationText text) => text switch
    {
        ValidationText.EmailRequired => UiText.Validation.EmailRequired,
        ValidationText.EmailInvalid => UiText.Validation.EmailInvalid,
        ValidationText.PasswordRequired => UiText.Validation.PasswordRequired,
        ValidationText.PasswordRepeatRequired => UiText.Validation.PasswordRepeatRequired,
        ValidationText.PasswordsDoNotMatch => UiText.Validation.PasswordsDoNotMatch,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "No catalogue entry for this refusal."),
    };
}

/// <summary><see cref="RequiredAttribute"/> whose message comes from the catalogue.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class LocalizedRequiredAttribute(ValidationText text) : RequiredAttribute
{
    public ValidationText Text { get; } = text;

    public override string FormatErrorMessage(string name) => LocalizedValidation.Message(Text);
}

/// <summary>
/// <see cref="EmailAddressAttribute"/>'s rule with the catalogue's message.
///
/// Composed rather than derived: <see cref="EmailAddressAttribute"/> is <c>sealed</c>, so the
/// subclass the other two here are will not compile. Reimplementing the check would be worse than
/// the problem — it is the framework's definition of a valid address and it must not fork — so the
/// real attribute is held as a field and asked, and only the sentence is ours.
///
/// One consequence, and it is fine here: MVC's client-side adapter keys off the built-in type, so
/// no <c>data-val-email</c> is emitted for this property. The panel does not load unobtrusive
/// validation, and the box is <c>type="email"</c>, which the browser checks itself.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class LocalizedEmailAddressAttribute(ValidationText text) : ValidationAttribute
{
    /// <summary>Stateless, so one instance answers every request.</summary>
    private static readonly EmailAddressAttribute Rule = new();

    public ValidationText Text { get; } = text;

    public override bool IsValid(object? value) => Rule.IsValid(value);

    public override string FormatErrorMessage(string name) => LocalizedValidation.Message(Text);
}

/// <summary><see cref="CompareAttribute"/> whose message comes from the catalogue.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class LocalizedCompareAttribute(string otherProperty, ValidationText text)
    : CompareAttribute(otherProperty)
{
    public ValidationText Text { get; } = text;

    public override string FormatErrorMessage(string name) => LocalizedValidation.Message(Text);
}
