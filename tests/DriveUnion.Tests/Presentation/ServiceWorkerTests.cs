using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriveUnion.Tests.Localization;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The service worker's address, its scope, and the list of what it is allowed to keep.
///
/// <para>Almost everything a service worker gets wrong, it gets wrong in silence. A worker served
/// from the wrong directory registers, installs, activates and controls nothing — <c>scope</c> is
/// the directory the script came from, so one at <c>/build/sw-a1b2c3.js</c> owns the stylesheets and
/// none of the pages. A second registration for the same scope does not fail either: it replaces the
/// worker that was there, so two features take it in turns to work depending on which ran last. And
/// a worker that caches one address too many is a customer's file names written to a phone in a
/// product sold on the server holding no readable copy, which nothing reports at all.</para>
///
/// <para><b>What this file can and cannot see.</b> The suite has no JavaScript engine, so these are
/// assertions about the worker's text and about what the application serves — the half of each
/// defect a C# test can hold, the same bargain <c>PanelLayoutTests</c> makes with the stylesheet.
/// The behaviour itself is held by <c>Scripts/sw.test.ts</c>, which evaluates
/// <c>wwwroot/sw.js</c> — the shipped bytes, not a bundled copy — against a stand-in Cache API and
/// asserts that a <c>/d/{slug}</c>, an <c>/api/</c> address and a page of the panel are neither
/// answered nor stored. Neither half is sufficient: this one would pass on a worker that read its
/// own allowlist and ignored it, and that one would pass on a worker nothing ever served.</para>
/// </summary>
public class ServiceWorkerTests
{
    /// <summary>Where the worker is served from, which is also the whole of its scope.</summary>
    private const string WorkerPath = "/sw.js";

    /// <summary>The file <c>sw.js</c> imports so M7 can add a push handler without a second worker.</summary>
    private const string PushSeamPath = "/sw-push.js";

    private const string OfflinePath = "/offline";

    // ------------------------------------------------------------------ the address and the scope

    /// <summary>
    /// <b>The worker is served, unhashed, from the root.</b>
    ///
    /// <para>A worker controls the directory it was served from and everything below it. This is the
    /// one assertion that stands between the design and the version of it where every page in the
    /// panel is outside the worker's scope and the offline page is unreachable — a state in which
    /// registration succeeds, installation succeeds, and nothing works.</para>
    /// </summary>
    [Fact]
    public async Task The_worker_is_served_from_the_root_of_its_scope()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(WorkerPath, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a 404 here is a panel with no offline page and nothing anywhere saying why");

        response.Content.Headers.ContentType?.MediaType.Should().Contain(
            "javascript",
            "a worker served as anything else is refused by the browser at registration");

        // One segment. The scope is the script's own directory, so any deeper path is a worker that
        // cannot see a single page of the panel.
        WorkerPath.TrimStart('/').Should().NotContain("/");
    }

    /// <summary>
    /// The push seam is served as well, because <c>importScripts</c> of a 404 throws.
    ///
    /// <para><c>sw.js</c> wraps the import so that failure costs push rather than the whole worker —
    /// but a seam that is not there is a seam M7 will replace with a second registration, which is
    /// the failure the seam exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task The_push_seam_is_a_file_that_exists()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(PushSeamPath, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// <b>One registration, and it names the worker at the root.</b>
    ///
    /// <para>This is the assertion M7 will run into. Two service workers cannot hold one scope:
    /// <c>register()</c> for a different script at <c>/</c> does not add a worker beside the one
    /// that is there, it replaces it. So the moment a second call appears anywhere in the bundle,
    /// one of the two features stops working — per page load, depending on which ran last, with
    /// nothing thrown and nothing logged.</para>
    /// </summary>
    [Fact]
    public void The_bundle_registers_exactly_one_worker()
    {
        var calls = new List<string>();

        foreach (var file in ScriptSources())
        {
            // Comments stripped, because the reason there is one call is written out beside it — in
            // prose that names the call it is warning about, twice.
            foreach (Match match in Regex.Matches(
                WithoutComments(File.ReadAllText(file)),
                @"serviceWorker\s*\.\s*register\s*\(\s*(?<url>[^,)]*)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
            {
                calls.Add($"{Path.GetFileName(file)}: {match.Groups["url"].Value.Trim()}");
            }
        }

        calls.Should().ContainSingle(
            "a second register() replaces the worker holding this scope rather than adding one — "
            + "Scripts/serviceWorker.ts exports serviceWorkerReady() so nothing has to call it again");

        calls[0].Should().Contain(
            "WorkerUrl",
            "the address is a named constant so that moving the file is a visible change");

        Read("src/DriveUnion.Web/Scripts/serviceWorker.ts").Should().Contain(
            $"const WorkerUrl = '{WorkerPath}'",
            "the registration and the file the application serves are one decision in two languages");
    }

    /// <summary>
    /// The seam M7 is meant to use is written down where M7 will be standing.
    ///
    /// <para>Three places, because there are three places somebody arrives at this problem from: the
    /// worker they want to add a handler to, the file that handler goes in, and the page-side call
    /// they need a registration for.</para>
    /// </summary>
    [Fact]
    public void The_worker_imports_the_push_seam_and_says_so_in_all_three_places()
    {
        Worker().Should().Contain(
            $"importScripts('{PushSeamPath}')",
            "this is the seam: the push handlers are a file of their own, imported synchronously so "
            + "they are registered before the first push can arrive");

        PushSeam().Should().Contain("push", "the seam file states the contract it is the seam for");

        // The other end of the same rule, and the easier one to get wrong: subscribing needs a
        // ServiceWorkerRegistration, and the obvious way to get one is the call that replaces the
        // worker.
        Read("src/DriveUnion.Web/Scripts/serviceWorker.ts").Should().Contain(
            "export function serviceWorkerReady",
            "M7 reaches pushManager through this rather than by registering a worker of its own");
    }

    /// <summary>
    /// The push file adds nothing that would race the caching rules.
    ///
    /// <para>Two <c>fetch</c> listeners race, the first to call <c>respondWith</c> wins, and which
    /// one that is depends on registration order — so a fetch handler in the push file is the
    /// caching rules being overridden by accident on some page loads and not others.</para>
    /// </summary>
    [Fact]
    public void The_push_seam_does_not_take_over_fetch()
    {
        WithoutComments(PushSeam()).Should().NotContain(
            "'fetch'",
            "sw.js owns fetch; a second listener here decides the caching rules by registration order");
    }

    /// <summary>
    /// The media seam does not take over fetch either, and answers through the one handler there is.
    ///
    /// <para>It is the one file that has a reason to want a <c>fetch</c> listener — it exists to
    /// answer requests — and that is exactly why it must not have one. Two listeners race and the
    /// winner depends on registration order, so a <c>/du1/</c> request would be answered by the
    /// caching rules on some page loads and by the decryptor on others. Instead it exposes
    /// <c>self.du1Media</c> and <c>sw.js</c> asks it, which keeps one handler and one place the
    /// routing order is decided.</para>
    /// </summary>
    [Fact]
    public void The_media_seam_does_not_take_over_fetch()
    {
        var media = WithoutComments(MediaSeam());

        media.Should().NotContain(
            "addEventListener('fetch'",
            "sw.js owns fetch; a listener here decides the routing order by registration order");

        media.Should().Contain(
            "self.du1Media",
            "the worker reaches the decryptor through this rather than through a second listener");

        WithoutComments(Worker()).Should().Contain(
            "du1Media",
            "sw.js has to actually ask, or the media route is a file nothing reads");
    }

    /// <summary>
    /// <b>Decrypted bytes are never stored, by anything.</b>
    ///
    /// <para>This is the same rule the cache allowlist keeps, at the one place it would be easiest
    /// to break: the media seam holds a customer's file with the lock taken off. A <c>caches.open</c>
    /// or an IndexedDB here would be a phone accumulating plaintext for a product sold on the server
    /// holding no readable copy — and it would be invisible, because everything would still work.</para>
    /// </summary>
    [Fact]
    public void The_media_seam_writes_decrypted_bytes_nowhere()
    {
        var media = WithoutComments(MediaSeam());

        foreach (var store in new[] { "caches", "indexedDB", "localStorage", "sessionStorage" })
        {
            media.Should().NotContain(
                store,
                $"a decrypted file must not reach {store} — it exists in memory and nowhere else");
        }

        // And the response says so itself, for every path out of it.
        media.Should().Contain(
            "'no-store'",
            "the element and any intermediary are told not to keep it either");
    }

    /// <summary>
    /// Its addresses are the worker's own and reach no route on the server.
    ///
    /// <para><c>/du1/</c> exists only inside the worker. If the prefix ever collided with a real
    /// route, a request the worker did not claim would fall through and be answered by the server
    /// with something that is not what the element asked for.</para>
    /// </summary>
    [Fact]
    public void Nothing_on_the_server_answers_for_the_media_prefix()
    {
        var routes = new DirectoryInfo(Path.Combine(RepositoryRoot().FullName, "src/DriveUnion.Web/Controllers"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f.FullName));

        foreach (var source in routes)
        {
            source.Should().NotContain(
                "\"/du1",
                "that prefix belongs to the service worker and must reach no controller");
        }
    }

    /// <summary>
    /// Both files are classic scripts.
    ///
    /// <para>A module service worker is Chromium-only — iOS has none, and iOS is the platform this
    /// whole phase exists for. A worker that fails to parse does not register, does not report, and
    /// leaves the panel exactly as it was, so this would be found by somebody with an iPhone and a
    /// suspicion rather than by anything failing.</para>
    /// </summary>
    [Theory]
    [InlineData("src/DriveUnion.Web/wwwroot/sw.js")]
    [InlineData("src/DriveUnion.Web/wwwroot/sw-push.js")]
    [InlineData("src/DriveUnion.Web/wwwroot/sw-media.js")]
    public void The_worker_is_a_classic_script(string path)
    {
        var source = WithoutComments(Read(path));

        Regex.IsMatch(source, @"^\s*(import|export)\s", RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            .Should().BeFalse($"{path} would need type: 'module', which iOS does not have");
    }

    // ------------------------------------------------------------------ what may be cached

    /// <summary>
    /// <b>The allowlist holds nothing but static assets.</b>
    ///
    /// <para>This is the product decision, and it is a decision rather than a scope cut: file names
    /// and workspace names must not sit on a phone's disk in a product whose whole claim is that the
    /// server holds no readable copy. The list is an allowlist and not a denylist for the same
    /// reason — a denylist is a list somebody has to remember to add to, so the next screen at a new
    /// address would be cached by default and the claim would stop being true without a line
    /// changing.</para>
    /// </summary>
    [Fact]
    public void Nothing_the_worker_may_cache_is_a_page_of_the_panel()
    {
        var cacheable = ArrayLiteral("Static");

        cacheable.Should().BeEquivalentTo(
            ["/build/", "/css/", "/fonts/", "/icons/"],
            "the shell and its assets, and widening this is a product decision rather than a tidy-up");

        // Every address a customer's own data is behind. None of them may be reachable from a
        // prefix on that list, and the arithmetic is written out rather than trusted to reading.
        var panel = new[]
        {
            "/", "/files", "/links", "/trash", "/keys", "/plans", "/telegram", "/design",
            "/operator/tenants", "/operator/plans", "/operator/abuse", "/operator/backups",
            "/Identity/Account/Login", "/api/uploads", "/api/files", "/api/v1/files", "/s3/bucket",
            "/d/kx91mzq4", "/d/kx91mzq4/file", "/d/kx91mzq4/preview", "/offline",
        };

        foreach (var path in panel)
        {
            cacheable.Should().NotContain(
                prefix => path.StartsWith(prefix, StringComparison.Ordinal),
                $"{path} is not a static asset and must not be storable");
        }
    }

    /// <summary>
    /// <b>The two addresses the worker has no code path for at all.</b>
    ///
    /// <para>Stronger than "not cached", and deliberately so. <c>/d/{slug}</c> is the address
    /// revocation is about: the whole point of revoking a link is that it stops working at once, and
    /// a worker that can answer for that path is a worker that can be made to answer it from disk by
    /// one later edit. <c>/api/</c> is the customer's catalogue and, while a 96 GB file is in
    /// flight, the transport that resumes it against the server's own byte count.</para>
    /// </summary>
    [Fact]
    public void The_share_link_and_the_api_are_refused_before_anything_else()
    {
        ArrayLiteral("NeverOurs").Should().BeEquivalentTo(
            ["/d/", "/api/"],
            "a revoked link must die at once, and a worker between a resumable upload and its "
            + "server is a place for a 96 GB transfer to go wrong for no benefit");

        var worker = WithoutComments(Worker());

        // The refusal is the first thing the fetch handler does after establishing that this is a
        // same-origin GET. Ordering is the whole of it: a navigation branch above this line would
        // answer /d/{slug} with the offline page, which is a worker deciding what a share link does.
        worker.IndexOf("NeverOurs.some", StringComparison.Ordinal).Should().BeLessThan(
            worker.IndexOf("'navigate'", StringComparison.Ordinal),
            "these are refused before the navigation branch, or the worker answers for /d/ after all");
    }

    /// <summary>
    /// Caching a page is not something the worker is capable of.
    ///
    /// <para>Every page of the panel is rendered for whoever asked: the sidebar carries their email,
    /// the table carries their file names, the shell carries an antiforgery token minted for their
    /// session. There is no subset of that HTML that is safe on a phone's disk, so the navigation
    /// path has no branch that writes.</para>
    /// </summary>
    [Fact]
    public void The_navigation_path_has_no_way_to_write_anything()
    {
        var navigation = Between(WithoutComments(Worker()), "async function navigation(", "\nasync function");

        navigation.Should().NotBeNullOrWhiteSpace();
        navigation.Should().NotContain("caches.open", "a navigation response is somebody's own page");
        navigation.Should().NotContain("store(", "a navigation response is somebody's own page");
        navigation.Should().Contain("caches.match", "reading the offline page is the whole of what it does");
    }

    // ------------------------------------------------------------------ the bundle it lives beside

    /// <summary>
    /// The worker is outside the bundle, and the bundle is unchanged by it.
    ///
    /// <para><c>ViteManifest</c> resolves <c>Scripts/main.ts</c> out of
    /// <c>wwwroot/build/manifest.json</c>, and that path is deliberate: the .NET SDK excludes
    /// dot-folders from <c>dotnet publish</c>, so Vite's own default hides the manifest out of the
    /// published image and the app comes up with no CSS at all. A second build input is the change
    /// most likely to disturb either fact.</para>
    /// </summary>
    [Fact]
    public void The_vite_build_still_has_one_entry_and_still_writes_the_manifest_where_publish_can_see_it()
    {
        var config = Read("src/DriveUnion.Web/vite.config.ts");

        config.Should().Contain(
            "manifest: 'manifest.json'",
            "Vite's default build/.vite/manifest.json is dropped by dotnet publish");

        // Comments stripped first: the note beside that line argues at length about the second entry
        // this must not grow, and counting the argument would be counting the wrong thing.
        Regex.Matches(WithoutComments(config), @"input:", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Should().ContainSingle("the worker is hand-written, not a second entry");

        config.Should().Contain("input: 'Scripts/main.ts'");

        // And nothing hashed the worker on its way out. wwwroot/build is Vite's output directory and
        // is emptied on every build; the worker is not in it and must never be.
        var build = new DirectoryInfo(
            Path.Combine(RepositoryRoot().FullName, "src/DriveUnion.Web/wwwroot/build"));

        if (build.Exists)
        {
            build.EnumerateFiles("sw*.js", SearchOption.AllDirectories)
                .Should().BeEmpty("a worker under /build/ has /build/ for a scope and controls no page");

            // The other half, checked here because this is the only test that looks in this
            // directory at all. Absent it means npm has not run, which is not a failure — the panel
            // is built to degrade to a server-rendered page when there is no bundle.
            var manifest = new FileInfo(Path.Combine(build.FullName, "manifest.json"));

            if (manifest.Exists)
            {
                JsonDocument.Parse(File.ReadAllText(manifest.FullName))
                    .RootElement.TryGetProperty("Scripts/main.ts", out _)
                    .Should().BeTrue("ViteManifest resolves the panel's bundle by this key");
            }
        }
    }

    // ------------------------------------------------------------------ the offline page

    /// <summary>
    /// <b>The one page that is written to a phone's disk carries nothing about anybody.</b>
    ///
    /// <para>It is fetched at install with the culture cookie on the request, so it is a real
    /// rendered page and not markup inside the worker — which is what keeps the product's Persian in
    /// <c>UiText</c> rather than in a JavaScript file nothing renders in both languages. The price
    /// of that is this test: a page the panel's shell had crept back onto would be a signed-in
    /// customer's email address stored on a device.</para>
    /// </summary>
    [Fact]
    public async Task The_offline_page_is_anonymous_and_the_same_for_everybody()
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri(OfflinePath, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the worker fetches this at install; a 404 is an install that stores nothing");

        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var html = await LocalizationHarness.TextAsync(response);

        html.Should().NotContain(
            "__RequestVerificationToken",
            "an antiforgery token is minted per session and this response is kept on a device");

        html.Should().NotContain("app-sidebar", "the panel shell carries the reader's own identity");
        html.Should().NotContain("sidebar-identity");
        html.Should().NotContain("data-upload-config");
        html.Should().NotContain("data-island", "the offline page may be shown with no bundle at all");
    }

    /// <summary>
    /// It is written in the panel's language, like every other user-visible string in the product.
    ///
    /// <para>The worker's install fetch carries the culture cookie — a Request built from a
    /// same-origin URL sends credentials — so what is stored is the language the panel was in.</para>
    /// </summary>
    [Fact]
    public async Task The_offline_page_speaks_the_panels_language()
    {
        var persian = await OfflineAsync("fa");
        var english = await OfflineAsync("en");

        persian.Should().Contain("آفلاین");
        persian.Should().Contain("dir=\"rtl\"");
        persian.Should().NotContain("Try again");

        english.Should().Contain("Offline");
        english.Should().Contain("dir=\"ltr\"");
        english.Should().NotContain("آفلاین");
    }

    private static async Task<string> OfflineAsync(string culture)
    {
        await using var harness = new LocalizationHarness();
        using var client = harness.NewClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, OfflinePath);
        request.Headers.Add("Cookie", LocalizationHarness.CultureCookie(culture));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Razor escapes everything outside Basic Latin, so an assertion against «آفلاین» would pass
        // on a page that says it and on a page that does not.
        return await LocalizationHarness.TextAsync(response);
    }

    // ------------------------------------------------------------------ reading the source

    /// <summary>One of the worker's declared prefix lists, as the strings it holds.</summary>
    private static List<string> ArrayLiteral(string name)
    {
        var match = Regex.Match(
            Worker(),
            $@"const {Regex.Escape(name)} = \[(?<items>[^\]]*)\]",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"wwwroot/sw.js declares no `const {name} = [...]`; it was renamed.");

        return [.. Regex
            .Matches(match.Groups["items"].Value, "'([^']*)'", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value)];
    }

    /// <summary>The body between a marker and the next one, so a function can be read on its own.</summary>
    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);

        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>
    /// Block and line comments removed.
    ///
    /// <para>These files are mostly comment, on purpose, and every one of them discusses the things
    /// being asserted against — <c>fetch</c>, <c>import</c>, <c>'navigate'</c>. Asserting against the
    /// prose would make this file agree with the explanations rather than with the code.</para>
    /// </summary>
    private static string WithoutComments(string source)
    {
        var blockless = Regex.Replace(
            source,
            @"/\*.*?\*/",
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        return Regex.Replace(
            blockless,
            @"//[^\n]*",
            string.Empty,
            RegexOptions.None,
            TimeSpan.FromSeconds(5));
    }

    private static string Worker() => Read("src/DriveUnion.Web/wwwroot/sw.js");

    private static string PushSeam() => Read("src/DriveUnion.Web/wwwroot/sw-push.js");

    private static string MediaSeam() => Read("src/DriveUnion.Web/wwwroot/sw-media.js");

    private static IEnumerable<string> ScriptSources() =>
        new DirectoryInfo(Path.Combine(RepositoryRoot().FullName, "src/DriveUnion.Web/Scripts"))
            .EnumerateFiles("*.ts", SearchOption.AllDirectories)
            .Select(file => file.FullName);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot().FullName, relativePath));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriveUnion.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            $"No DriveUnion.slnx above {AppContext.BaseDirectory}; this test reads the repository's own source.");
    }
}
