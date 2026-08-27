using System.Text.RegularExpressions;
using DriveUnion.Web.Infrastructure;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The three colours that live outside the stylesheet, and the only thing that can notice when they
/// stop agreeing with it.
///
/// <para><c>BrandColours</c> exists because a web app manifest is JSON and a <c>theme-color</c> meta
/// tag is an attribute — neither can hold a CSS custom property, and both are read by the operating
/// system before any stylesheet has been fetched. Its values were sampled from the oklch tokens by
/// painting them into a canvas and reading the pixel back.</para>
///
/// <para>Nothing compares the two after that. Change <c>--bg</c> in <c>tokens.css</c> and the panel
/// changes colour while the splash screen behind the app icon keeps the old one, which shows up as a
/// flash of the wrong colour every time the app starts and as nothing at all in a diff. This reads
/// the stylesheet and fails if the values these were taken from are no longer in it.</para>
/// </summary>
public class BrandColourTests
{
    [Fact]
    public void The_manifest_colours_were_taken_from_tokens_that_are_still_there()
    {
        var tokens = Read("src/DriveUnion.Web/wwwroot/css/tokens.css");

        tokens.Should().Contain(
            BrandColours.Tokens.LightBackground,
            $"BrandColours.LightBackground ({BrandColours.LightBackground}) is the sRGB of this token, "
                + "and the splash screen behind the app icon is painted with it");

        tokens.Should().Contain(
            BrandColours.Tokens.DarkBackground,
            $"BrandColours.DarkBackground ({BrandColours.DarkBackground}) is the sRGB of this token, "
                + "and the dark status bar is painted with it");

        // The accent is a hex literal in the stylesheet rather than an oklch, so this one is a copy.
        // Compared without case because the stylesheet writes #0F9D77 and the icons write #0f9d77 —
        // the same colour, and neither spelling is worth making the other one wrong.
        tokens.Should().ContainEquivalentOf(
            BrandColours.Accent,
            "the accent is copied rather than converted, so it must match");
    }

    /// <summary>
    /// The icons are painted with the brand colour too, and they are the one place it is baked into
    /// a file rather than read from a variable.
    ///
    /// <para>An icon is drawn once and then never looked at again — it is on a home screen, not on a
    /// screen anybody reviews. Change the accent and every surface in the product follows except the
    /// five PNGs, which keep the old colour until somebody happens to notice their phone.</para>
    /// </summary>
    [Fact]
    public void The_app_icon_is_painted_with_the_brand_colour()
    {
        foreach (var icon in new[] { "icon.svg", "icon-maskable.svg" })
        {
            Read($"src/DriveUnion.Web/wwwroot/icons/{icon}").Should().ContainEquivalentOf(
                BrandColours.Accent,
                $"{icon} is the source the home-screen PNGs are rendered from");
        }
    }

    /// <summary>
    /// The PNGs a phone actually reads exist, and are PNGs.
    ///
    /// <para>They are generated rather than written, so the failure to guard against is a file that
    /// is there and is not an image — an empty file, or an error page saved under the wrong name.
    /// iOS answers that by putting a screenshot of the sign-in form on the home screen instead.</para>
    /// </summary>
    [Fact]
    public void Every_rendered_icon_is_a_real_png()
    {
        string[] rendered =
        [
            "icon-192.png",
            "icon-512.png",
            "icon-maskable-512.png",
            "apple-touch-icon.png",
            "favicon-32.png",
        ];

        foreach (var name in rendered)
        {
            var path = Path.Combine(RepositoryRoot(), "src/DriveUnion.Web/wwwroot/icons", name);

            File.Exists(path).Should().BeTrue($"{name} is referenced by the manifest or the shell");

            var header = new byte[8];
            using (var file = File.OpenRead(path)) file.ReadExactly(header);

            Convert.ToHexString(header).Should().Be(
                "89504E470D0A1A0A", $"{name} has to actually be a PNG, not a file named like one");
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    /// <summary>
    /// They are the shape a manifest and a meta tag can actually use.
    ///
    /// <para>A colour a browser cannot parse is ignored, and an ignored <c>background_color</c> is a
    /// white splash screen in a dark app — which reads as a broken launch and reports as nothing.
    /// Six-digit lower-case hex, which every reader of both handles.</para>
    /// </summary>
    [Fact]
    public void They_are_plain_six_digit_hex()
    {
        string[] colours =
        [
            BrandColours.Accent,
            BrandColours.LightBackground,
            BrandColours.DarkBackground,
        ];

        foreach (var colour in colours)
        {
            Regex.IsMatch(colour, "^#[0-9a-fA-F]{6}$", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Should().BeTrue($"{colour} has to be readable by a manifest parser and a meta tag");
        }

        colours.Should().OnlyHaveUniqueItems("three names for one colour would be a mistake, not a palette");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("DriveUnion.slnx was not found above the test binaries.");
    }
}
