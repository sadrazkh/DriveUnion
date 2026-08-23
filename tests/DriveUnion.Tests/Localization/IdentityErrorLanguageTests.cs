using DriveUnion.Web.Localization;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// Identity's own refusals, which are English resources inside the framework and appear untranslated
/// inside a Persian first-run screen.
///
/// <see cref="DriveUnionIdentityErrorDescriber"/> is written and is deliberately <b>not</b>
/// registered — the registration is one line in Program.cs, which this slice does not edit, and
/// there is an existing test in <c>Identity/FirstRunSetupTests</c> that asserts Identity's English
/// sentence on the rendered page and would have to change in the same commit. So what is proved here
/// is that the type is correct and ready, not that the panel uses it. See Localization/README.md.
/// </summary>
public class IdentityErrorLanguageTests
{
    private readonly DriveUnionIdentityErrorDescriber describer = new();

    [Fact]
    public void In_english_the_words_are_identitys_own()
    {
        using var scope = CultureScope.English();

        var stock = new IdentityErrorDescriber();

        // Not "looks English" — literally the framework's sentence. A transcription here would be a
        // second copy of a string the framework owns and changes.
        describer.PasswordTooShort(10).Description.Should().Be(stock.PasswordTooShort(10).Description);
        describer.PasswordRequiresDigit().Description.Should().Be(stock.PasswordRequiresDigit().Description);
        describer.DuplicateEmail("a@b.test").Description.Should().Be(stock.DuplicateEmail("a@b.test").Description);

        // And it is still the sentence the shipped screen shows today.
        describer.PasswordTooShort(10).Description.Should().Contain("must be at least 10 characters");
    }

    [Fact]
    public void In_persian_the_words_are_ours()
    {
        using var scope = CultureScope.Persian();

        describer.PasswordTooShort(10).Description.Should().Be("گذرواژه باید دست‌کم ۱۰ نویسه داشته باشد.");
        describer.PasswordRequiresDigit().Description.Should().Be("گذرواژه باید دست‌کم یک رقم داشته باشد.");
        describer.DuplicateEmail("a@b.test").Description.Should().Contain("a@b.test");
    }

    /// <summary>
    /// The figure inside a Persian sentence is a figure in prose, so it takes Persian digits — the
    /// same rule the first-run screen's rule list follows, and not a second one.
    /// </summary>
    [Fact]
    public void A_length_inside_a_persian_sentence_is_written_in_persian_digits()
    {
        using var scope = CultureScope.Persian();

        describer.PasswordRequiresUniqueChars(4).Description.Should().Contain("۴");
        describer.PasswordRequiresUniqueChars(4).Description.Should().NotContain("4");
    }

    /// <summary>
    /// The code is what callers switch on, and it must not depend on which language the request was
    /// in. Every override takes it from the framework rather than restating it.
    /// </summary>
    [Fact]
    public void The_code_is_the_frameworks_in_both_languages()
    {
        var stock = new IdentityErrorDescriber();

        using (CultureScope.Persian())
        {
            describer.PasswordTooShort(10).Code.Should().Be(stock.PasswordTooShort(10).Code);
            describer.InvalidEmail("nope").Code.Should().Be(stock.InvalidEmail("nope").Code);
            describer.ConcurrencyFailure().Code.Should().Be(stock.ConcurrencyFailure().Code);
        }

        using (CultureScope.English())
        {
            describer.DefaultError().Code.Should().Be(stock.DefaultError().Code);
        }
    }

    /// <summary>
    /// Every error <c>UserManager.CreateAsync(user, password)</c> can produce on the first-run form —
    /// the whole reachable surface in M1 — says something in Persian. An override that was forgotten
    /// shows up as an English sentence in the middle of a Persian card, which is exactly the defect
    /// this type exists to remove.
    /// </summary>
    [Fact]
    public void Nothing_the_first_run_form_can_produce_is_left_in_english()
    {
        using var scope = CultureScope.Persian();

        IdentityError[] reachable =
        [
            describer.PasswordTooShort(10),
            describer.PasswordRequiresUniqueChars(4),
            describer.PasswordRequiresNonAlphanumeric(),
            describer.PasswordRequiresDigit(),
            describer.PasswordRequiresLower(),
            describer.PasswordRequiresUpper(),
            describer.DuplicateEmail("a@b.test"),
            describer.DuplicateUserName("a@b.test"),
            describer.InvalidEmail("nope"),
            describer.InvalidUserName("nope"),
            describer.DefaultError(),
            describer.ConcurrencyFailure(),
        ];

        foreach (var error in reachable)
        {
            error.Description.Should().NotBeNullOrWhiteSpace();
            error.Description.Should().MatchRegex(
                "[\u0600-\u06FF]",
                $"«{error.Code}» can be shown on the first-run screen and has no Persian wording");
        }
    }
}
