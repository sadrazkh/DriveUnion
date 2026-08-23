using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// The numeral rule, which is about direction rather than language, now that there are two
/// languages to test it against.
///
/// The rule itself has not moved: digits set in prose take that prose's numerals, digits in an LTR
/// technical readout stay Latin. What English changes is which side of the line most of the panel's
/// figures fall on — English prose is LTR, so both sides agree and the Persian path is simply not
/// taken. <see cref="PersianDigits"/> is untouched and still says what the rule is.
/// </summary>
public class NumeralTests
{
    [Fact]
    public void A_count_in_persian_prose_is_written_in_persian_digits()
    {
        using var scope = CultureScope.Persian();

        Numerals.Count(14286).Should().Be("۱۴٬۲۸۶");
        Numerals.Plain(1405).Should().Be("۱۴۰۵");
        Numerals.Percent(68).Should().Be("۶۸٪");
    }

    [Fact]
    public void The_same_count_in_english_prose_is_latin()
    {
        using var scope = CultureScope.English();

        Numerals.Count(14286).Should().Be("14,286");
        Numerals.Plain(1405).Should().Be("1405");
        Numerals.Percent(68).Should().Be("68%");
    }

    [Fact]
    public void The_persian_path_is_the_one_that_was_already_there()
    {
        using var scope = CultureScope.Persian();

        // Not a reimplementation: what the panel wrote before there was an English rendering is what
        // it still writes, character for character.
        Numerals.Count(14286).Should().Be(PersianDigits.Count(14286));
        Numerals.Plain(9).Should().Be(PersianDigits.Plain(9));
        Numerals.Percent(61.4).Should().Be(PersianDigits.Percent(61.4));
        Numerals.InProse("۱۴۰۵/۰۵/۳۱").Should().Be(PersianDigits.Translate("۱۴۰۵/۰۵/۳۱"));
    }

    [Fact]
    public void An_assembled_sentence_follows_the_language_it_is_assembled_in()
    {
        using (CultureScope.Persian())
        {
            Numerals.InProse("امروز 10:22").Should().Be("امروز ۱۰:۲۲");
        }

        using (CultureScope.English())
        {
            Numerals.InProse("Today 10:22").Should().Be("Today 10:22");
        }
    }

    /// <summary>
    /// A byte size is a technical readout in both languages, and it is formatted by
    /// <c>DisplayFormats</c> with the invariant culture — so it must not have quietly become a
    /// candidate for translation just because the panel gained a second language.
    /// </summary>
    [Fact]
    public void A_byte_size_is_latin_in_both_languages()
    {
        using (CultureScope.Persian())
        {
            DisplayFormats.Bytes(19_293_798).Should().Be("18.4 MB");
        }

        using (CultureScope.English())
        {
            DisplayFormats.Bytes(19_293_798).Should().Be("18.4 MB");
        }
    }

    /// <summary>The password policy the first-run screen prints, in whichever language it prints it.</summary>
    [Fact]
    public void The_password_rules_carry_the_prose_numerals_of_their_own_language()
    {
        using (CultureScope.Persian())
        {
            // The exact sentence the shipped screen has always shown, unchanged by this slice.
            UiText.Identity.PasswordMinimumLength(10).Should().Be("دست‌کم ۱۰ نویسه");
        }

        using (CultureScope.English())
        {
            UiText.Identity.PasswordMinimumLength(10).Should().Be("at least 10 characters");
        }
    }
}
