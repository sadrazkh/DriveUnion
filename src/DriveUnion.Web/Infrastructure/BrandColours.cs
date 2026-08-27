namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// The three colours that have to exist outside CSS.
///
/// <para>Everything this product paints reads its colour from <c>tokens.css</c>, and that is where
/// colour belongs. These three cannot: a web app manifest is JSON, and a <c>theme-color</c> meta tag
/// is an attribute — neither can hold a custom property, and both are read by the operating system
/// before any stylesheet has been fetched. That is the whole reason this file exists, rather than a
/// second opinion about the palette.</para>
///
/// <para><b>They are sRGB conversions of the oklch tokens</b>, sampled by painting each token into a
/// canvas and reading the pixel back, so they are the browser's own answer rather than an
/// approximation. <c>BrandColourTests</c> reads <c>tokens.css</c> and fails if the oklch values these
/// were taken from have changed — which is the only way to notice, since nothing else compares
/// them.</para>
/// </summary>
public static class BrandColours
{
    /// <summary>
    /// <c>--accent</c>. Already a hex literal in the stylesheet, so this one is a copy rather than a
    /// conversion.
    /// </summary>
    public const string Accent = "#0f9d77";

    /// <summary>
    /// <c>--bg</c> in the light theme: <c>oklch(0.975 0.005 160)</c>.
    ///
    /// <para>The manifest's <c>background_color</c>, which is what a launcher paints behind the icon
    /// while the app is starting. The page's own background and not the accent, because the point is
    /// that the splash and the first paint are the same colour and the start looks like nothing
    /// happened.</para>
    /// </summary>
    public const string LightBackground = "#f4f8f6";

    /// <summary>
    /// <c>--bg</c> in the dark theme: <c>oklch(0.185 0.008 165)</c>.
    /// </summary>
    public const string DarkBackground = "#0f1412";

    /// <summary>
    /// The oklch each converted value was taken from, so a test can find them in the stylesheet.
    ///
    /// <para>Spelled exactly as <c>tokens.css</c> spells them, whitespace and all.</para>
    /// </summary>
    public static class Tokens
    {
        public const string LightBackground = "oklch(0.975 0.005 160)";

        public const string DarkBackground = "oklch(0.185 0.008 165)";
    }
}
