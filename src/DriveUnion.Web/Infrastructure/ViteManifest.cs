using System.Text.Json;

namespace DriveUnion.Web.Infrastructure;

/// <summary>
/// Reads Vite's build manifest so Razor can reference hashed, content-addressed island bundles.
/// This is how Vue is "embedded" — Vite compiles the islands into wwwroot/build and Razor pulls
/// the right hashed file in. No separate SPA server, no Node process in production.
///
/// The manifest lives at wwwroot/build/manifest.json (configured in vite.config.ts). Vite's own
/// default, build/.vite/manifest.json, is probed second and only as a courtesy to a checkout built
/// before that setting existed: the .NET SDK excludes dot-folders from <c>dotnet publish</c>, so a
/// manifest that only exists in the hidden location never reaches the image and the app comes up
/// with no CSS at all.
///
/// Loading is lazy and retried so the app cannot get wedged if the assets land after startup —
/// which is exactly what happens when `npm run build` is still running against a live `dotnet watch`.
/// </summary>
public sealed class ViteManifest
{
    private readonly string[] _candidatePaths;
    private readonly bool _devServer;
    private readonly string _devServerUrl;
    private Dictionary<string, ViteChunk>? _chunks;

    public ViteManifest(IWebHostEnvironment env, IConfiguration config)
    {
        _devServer = config.GetValue("Vite:UseDevServer", false);
        _devServerUrl = config["Vite:DevServerUrl"] ?? "http://localhost:5173";
        _candidatePaths =
        [
            Path.Combine(env.WebRootPath, "build", "manifest.json"),
            Path.Combine(env.WebRootPath, "build", ".vite", "manifest.json"),
        ];
        if (!_devServer) TryLoad();
    }

    public bool UseDevServer => _devServer;

    public string DevServerUrl => _devServerUrl;

    /// <summary>
    /// Resolves a manifest key (the entry's path as Vite saw it, e.g. "Scripts/main.ts") to the
    /// hashed script and its stylesheets. Returns nulls rather than throwing: a missing bundle
    /// must degrade to a server-rendered page, not a 500 on someone's download link.
    /// </summary>
    public (string? Js, IReadOnlyList<string> Css) Resolve(string entry)
    {
        if (_chunks is null && !_devServer) TryLoad(); // retry — assets may land after startup

        if (_chunks is null || !_chunks.TryGetValue(entry, out var chunk))
            return (null, Array.Empty<string>());

        var css = chunk.Css?.Select(c => "/build/" + c).ToList() ?? [];
        return ("/build/" + chunk.File, css);
    }

    private void TryLoad()
    {
        foreach (var path in _candidatePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                _chunks = JsonSerializer.Deserialize<Dictionary<string, ViteChunk>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return;
            }
            catch (Exception)
            {
                // Corrupt or half-written manifest (Vite mid-build) — try the next candidate, and
                // leave _chunks null so the next Resolve retries rather than caching the failure.
            }
        }
    }

    public sealed class ViteChunk
    {
        public string File { get; set; } = string.Empty;

        public List<string>? Css { get; set; }
    }
}
