using Microsoft.AspNetCore.Identity;

namespace DriveUnion.Web.Localization;

/// <summary>
/// Identity's own refusals, in the language of the page they land on.
///
/// ASP.NET Core Identity writes its errors in English from resources inside
/// <c>Microsoft.Extensions.Identity.Core</c>, and ships no Persian satellite — so «Passwords must be
/// at least 10 characters.» currently appears verbatim inside a Persian first-run screen. That was a
/// deliberate choice while there was one language (the rule that was broken, in the words of the
/// thing that broke it, beats a sentence of ours guessing at the policy), and it stops being
/// defensible the moment the panel has a Persian reader who never asked for English.
///
/// <para><b>Not registered.</b> Wiring it is one line in Program.cs, which this file's author does
/// not own — see Localization/README.md for the line and for the one existing test that asserts
/// Identity's English sentence and would have to be updated in the same change. Until then this
/// type is exercised only by <c>IdentityErrorLanguageTests</c>.</para>
///
/// The English side is <c>base</c> rather than a transcription, on purpose: those sentences are
/// Identity's, they change with the framework, and a copy of them here would be a second version of
/// the truth that nobody would notice going stale. Every override keeps <c>base</c>'s
/// <see cref="IdentityError.Code"/> for the same reason — the code is what callers switch on, and it
/// must never depend on which language the request happened to be in.
///
/// Only the errors reachable from a screen are overridden. <c>UserManager.CreateAsync(user,
/// password)</c> on the first-run form is the whole surface in M1: there is no password change, no
/// reset, no role management and no two-factor, so the rest of <see cref="IdentityErrorDescriber"/>
/// cannot be reached by anything a person can click.
/// </summary>
public sealed class DriveUnionIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => InPersian(
        base.DefaultError(),
        "خطای ناشناخته‌ای رخ داد.");

    public override IdentityError ConcurrencyFailure() => InPersian(
        base.ConcurrencyFailure(),
        "این حساب هم‌زمان جای دیگری تغییر کرده است. صفحه را تازه کنید و دوباره تلاش کنید.");

    public override IdentityError PasswordTooShort(int length) => InPersian(
        base.PasswordTooShort(length),
        $"گذرواژه باید دست‌کم {Numerals.Plain(length)} نویسه داشته باشد.");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => InPersian(
        base.PasswordRequiresUniqueChars(uniqueChars),
        $"گذرواژه باید دست‌کم {Numerals.Plain(uniqueChars)} نویسه‌ی متفاوت داشته باشد.");

    public override IdentityError PasswordRequiresNonAlphanumeric() => InPersian(
        base.PasswordRequiresNonAlphanumeric(),
        "گذرواژه باید دست‌کم یک نشانه داشته باشد، مانند ! یا # یا ?.");

    public override IdentityError PasswordRequiresDigit() => InPersian(
        base.PasswordRequiresDigit(),
        "گذرواژه باید دست‌کم یک رقم داشته باشد.");

    public override IdentityError PasswordRequiresLower() => InPersian(
        base.PasswordRequiresLower(),
        "گذرواژه باید دست‌کم یک حرف کوچک لاتین (a تا z) داشته باشد.");

    public override IdentityError PasswordRequiresUpper() => InPersian(
        base.PasswordRequiresUpper(),
        "گذرواژه باید دست‌کم یک حرف بزرگ لاتین (A تا Z) داشته باشد.");

    public override IdentityError DuplicateEmail(string email) => InPersian(
        base.DuplicateEmail(email),
        $"ایمیل {email} قبلاً ثبت شده است.");

    public override IdentityError DuplicateUserName(string userName) => InPersian(
        base.DuplicateUserName(userName),
        $"نام کاربری {userName} قبلاً ثبت شده است.");

    public override IdentityError InvalidEmail(string? email) => InPersian(
        base.InvalidEmail(email),
        $"ایمیل {email} معتبر نیست.");

    public override IdentityError InvalidUserName(string? userName) => InPersian(
        base.InvalidUserName(userName),
        $"نام کاربری {userName} معتبر نیست؛ فقط حرف و رقم مجاز است.");

    /// <summary>
    /// Identity's error with our sentence on it, or Identity's own untouched. The code always comes
    /// from <paramref name="english"/> so it cannot drift from the framework's.
    /// </summary>
    private static IdentityError InPersian(IdentityError english, string description) =>
        PanelCulture.IsPersian
            ? new IdentityError { Code = english.Code, Description = description }
            : english;
}
