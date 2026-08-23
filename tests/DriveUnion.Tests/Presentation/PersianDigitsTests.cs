using System.Globalization;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Models;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The numeral rule, which the handoff states by drawing it rather than by naming it: «۲۴۱ دانلود»
/// and «۱۴٬۲۸۶ آیتم» sit on the same screen as <c>18.4 MB</c>, <c>341 MB/s</c> and
/// <c>/d/kx91mz</c>.
///
/// The rule is direction, not language. Digits set in Persian prose are Persian; digits in an LTR
/// technical readout — byte sizes, speeds, latencies, slugs, ISO dates — stay Latin, because those
/// are values somebody copies, greps or reads out. There is exactly one implementation of the
/// Persian half (<see cref="PersianDigits"/>) and one of the Latin half
/// (<see cref="DisplayFormats"/>), and these tests are what keeps a second one from being written.
/// </summary>
public class PersianDigitsTests
{
    /// <summary>
    /// 2026-08-22, midday in Tehran. Midday because the display zone falls back to UTC on a
    /// container without tzdata, and a rule about numerals must not fail over a missing time zone.
    /// </summary>
    private static readonly DateTimeOffset Moment =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(3.5));

    [Fact]
    public void Count_groups_with_the_arabic_thousands_separator()
    {
        PersianDigits.Count(14286).Should().Be("۱۴٬۲۸۶");
        PersianDigits.Count(241).Should().Be("۲۴۱");
        PersianDigits.Count(0).Should().Be("۰");
    }

    [Fact]
    public void Plain_leaves_a_bare_figure_ungrouped()
    {
        // A year is not a quantity: «۱۴۰۵» never wants a separator.
        PersianDigits.Plain(1405).Should().Be("۱۴۰۵");
        PersianDigits.Plain(3).Should().Be("۳");
    }

    [Fact]
    public void Percent_rounds_and_uses_the_arabic_percent_sign()
    {
        PersianDigits.Percent(68).Should().Be("۶۸٪");
        PersianDigits.Percent(81.6).Should().Be("۸۲٪");
        PersianDigits.Percent(0).Should().Be("۰٪");
    }

    [Fact]
    public void Translate_rewrites_digits_and_separators_and_leaves_the_prose_alone()
    {
        PersianDigits.Translate("1405/05/31").Should().Be("۱۴۰۵/۰۵/۳۱");
        PersianDigits.Translate("امروز 10:22").Should().Be("امروز ۱۰:۲۲");
        PersianDigits.Translate("14,286 آیتم").Should().Be("۱۴٬۲۸۶ آیتم");
        PersianDigits.Translate("3.5 روز").Should().Be("۳٫۵ روز");
        PersianDigits.Translate("68%").Should().Be("۶۸٪");
    }

    [Fact]
    public void Nothing_to_translate_is_an_empty_string_rather_than_a_null()
    {
        // Razor prints a null as nothing anyway; returning one only moves the question to the
        // caller, where the answer is the same.
        PersianDigits.Translate(null).Should().BeEmpty();
        PersianDigits.Translate(string.Empty).Should().BeEmpty();
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fa-IR")]
    public void The_output_does_not_move_with_the_thread_culture(string culture)
    {
        // de-DE groups with '.' and fa-IR can render native digits of its own. Grouping happens
        // against the invariant culture and is translated afterwards, so neither reaches the page.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            PersianDigits.Count(14286).Should().Be("۱۴٬۲۸۶");
            PersianDigits.Percent(68).Should().Be("۶۸٪");
            DisplayFormats.Bytes(19_293_798).Should().Be("18.4 MB");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void An_ltr_technical_readout_keeps_latin_digits()
    {
        DisplayFormats.Bytes(19_293_798).Should().Be("18.4 MB");
        DisplayFormats.Bytes(214L * 1024 * 1024 * 1024).Should().Be("214 GB");
        DisplayFormats.Bytes(512).Should().Be("512 B");
        DisplayFormats.Bytes(1024).Should().Be("1 KB");
        DisplayFormats.IsoDate(Moment).Should().Be("2026-08-22");
        PublicLinkFormatter.Display("https://yourdomain.com", "kx91mzq4")
            .Should().Be("yourdomain.com/d/kx91mzq4");
    }

    [Fact]
    public void Persian_prose_gets_persian_digits()
    {
        DisplayFormats.PersianDate(Moment).Should().Be("۱۴۰۵/۰۵/۳۱");
        DisplayFormats.RelativeFa(Moment, Moment.AddDays(3)).Should().Be("۳ روز پیش");
        $"{PersianDigits.Count(241)} بار دانلود شده".Should().Be("۲۴۱ بار دانلود شده");
    }

    [Fact]
    public void A_size_and_a_date_beside_each_other_use_different_numerals()
    {
        // The detail panel prints both in one line. This is the whole rule in one assertion, and
        // the reason a font-level substitution such as Vazirmatn FD cannot be used: it would turn
        // the size Persian too, with no way back.
        var line = $"{DisplayFormats.Bytes(19_293_798)} · {DisplayFormats.PersianDate(Moment)}";

        line.Should().Be("18.4 MB · ۱۴۰۵/۰۵/۳۱");
    }
}
