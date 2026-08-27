using System.Text.Json;
using System.Text.Json.Serialization;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The web app manifest — what a phone reads when somebody adds this to their home screen.
///
/// <para><b>Why a controller and not a file in wwwroot.</b> The manifest carries the product's name,
/// its short name and its description, and every other user-visible string in this product comes out
/// of <c>UiText</c> and is chosen by <c>PanelCulture</c>. A static file would be a fourth place where
/// Persian and English are written down, and the one place nothing renders in both — so it would
/// drift, and the drift would show up as an English icon label under a Persian panel.</para>
///
/// <para><b>That costs one attribute on the link tag.</b> A manifest is fetched with
/// <c>crossorigin="anonymous"</c> semantics unless the tag says otherwise, meaning no cookies — and
/// without the culture cookie this action would answer in the default language every time, which is
/// the exact failure the controller exists to avoid. <c>_HeadAssets.cshtml</c> carries
/// <c>crossorigin="use-credentials"</c>, and the two halves only work together.</para>
///
/// <para><b>Anonymous, deliberately.</b> The sign-in page wears the panel shell and is the first
/// thing a new customer sees; being unable to install from it would be strange. Nothing here is
/// scoped to a workspace and nothing in it is worth authenticating for.</para>
/// </summary>
[AllowAnonymous]
public sealed class ManifestController : Controller
{
    /// <summary>
    /// Where the app opens, and the boundary of what counts as "inside" it.
    ///
    /// <para>The root, which resolves to the dashboard for somebody signed in and the sign-in page
    /// for somebody who is not — the same behaviour as typing the address. A narrower start_url
    /// would mean an installed app that opens on a screen a signed-out visitor cannot see.</para>
    ///
    /// <para><c>scope</c> covers <c>/d/{slug}</c> as well, so a public link tapped by somebody who
    /// has the app installed opens inside it rather than bouncing out to a browser tab.</para>
    /// </summary>
    private const string Root = "/";

    [HttpGet("/manifest.webmanifest")]
    public IActionResult Get()
    {
        var manifest = new WebManifest
        {
            // A stable identity, so a later change to start_url is recognised as the same app being
            // updated rather than a second one appearing beside the first on the home screen.
            Id = Root,
            Name = UiText.Pwa.Name,
            ShortName = UiText.Pwa.ShortName,
            Description = UiText.Pwa.Description,
            Language = PanelCulture.Code,
            Direction = PanelCulture.Direction,
            StartUrl = Root,
            Scope = Root,

            // standalone: no browser chrome. There is no address bar to lose anything by — the panel
            // has its own navigation, and Scripts/navigate.ts already swaps content without a page
            // load, so back and forward inside the app are the app's own business.
            Display = "standalone",

            // The panel is a fixed-width sidebar beside a content column and is used on a phone held
            // upright. Locking it is not the point; "portrait" is a preference a launcher may ignore,
            // and on the devices that honour it, it stops the layout being thrown at a landscape
            // width it has no breakpoint for.
            Orientation = "portrait",

            // The launcher paints this behind the icon while the app starts. The page's own
            // background rather than the accent, so the splash and the first paint are the same
            // colour and starting up looks like nothing happened.
            BackgroundColor = BrandColours.LightBackground,

            // The system chrome around the app — the status bar on Android. The manifest holds one
            // value and cannot vary by theme, so this is the light one and the dark half is done with
            // a media-queried <meta name="theme-color"> in _HeadAssets.cshtml, which browsers prefer
            // over the manifest when both are present.
            ThemeColor = BrandColours.LightBackground,

            Icons =
            [
                new ManifestIcon("/icons/icon-192.png", "192x192", "image/png", "any"),
                new ManifestIcon("/icons/icon-512.png", "512x512", "image/png", "any"),

                // Separate rather than `purpose: "any maskable"` on one entry. A launcher told an
                // icon is both will happily mask the un-padded one, and the outer two discs are the
                // first thing a circular mask takes off.
                new ManifestIcon("/icons/icon-maskable-512.png", "512x512", "image/png", "maskable"),
            ],
        };

        // no-store rather than a long cache. It is a few hundred bytes read once at install time, and
        // it varies by a cookie — a shared cache holding one language's copy and handing it to
        // everybody is the failure that would be hardest to see and slowest to expire.
        Response.Headers.CacheControl = "no-store";

        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,

            // The registered type. application/json is widely tolerated and is still wrong, and at
            // least one linter refuses a manifest served as it.
            ContentType = "application/manifest+json",
            Content = JsonSerializer.Serialize(manifest, ManifestJson),
        };
    }

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The manifest carries a product name that is Persian. Escaping it would be valid JSON and an
        // unreadable file, and this is one of the few responses in the product a person opens by
        // hand to find out why an install looks wrong.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// The manifest's own shape. Property names are the specification's, which are snake_case and are
    /// spelled out rather than produced by a naming policy — <c>start_url</c> is not what any policy
    /// would make of <c>StartUrl</c>, and a manifest with one key subtly wrong fails silently.
    /// </summary>
    private sealed class WebManifest
    {
        [JsonPropertyName("id")] public required string Id { get; init; }

        [JsonPropertyName("name")] public required string Name { get; init; }

        [JsonPropertyName("short_name")] public required string ShortName { get; init; }

        [JsonPropertyName("description")] public required string Description { get; init; }

        [JsonPropertyName("lang")] public required string Language { get; init; }

        [JsonPropertyName("dir")] public required string Direction { get; init; }

        [JsonPropertyName("start_url")] public required string StartUrl { get; init; }

        [JsonPropertyName("scope")] public required string Scope { get; init; }

        [JsonPropertyName("display")] public required string Display { get; init; }

        [JsonPropertyName("orientation")] public required string Orientation { get; init; }

        [JsonPropertyName("background_color")] public required string BackgroundColor { get; init; }

        [JsonPropertyName("theme_color")] public required string ThemeColor { get; init; }

        [JsonPropertyName("icons")] public required IReadOnlyList<ManifestIcon> Icons { get; init; }
    }

    private sealed record ManifestIcon(
        [property: JsonPropertyName("src")] string Src,
        [property: JsonPropertyName("sizes")] string Sizes,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("purpose")] string Purpose);
}
