using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Tests.Hosting;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// DriveUnion.Web's real pipeline, localisation and all.
///
/// The panel's language is resolved by the framework's <c>RequestLocalizationMiddleware</c>, which
/// Program.cs now registers itself — <c>AddDriveUnionLocalization()</c> beside the other services
/// and <c>UseRequestLocalization()</c> after the static files. This harness used to make those two
/// registrations of its own through an <see cref="IStartupFilter"/>, because Program.cs was not the
/// founding slice's to edit; both are gone, and every test in this folder is now asking the shipped
/// pipeline rather than a copy of it.
///
/// SQLite in memory for the same reason every other web harness here uses it: the Data Protection
/// key ring is a table, and the antiforgery token on the language switch is protected with it.
/// </summary>
public sealed class LocalizationHarness : WebApplicationFactory<Program>
{
    /// <summary>The page every test in this folder renders: the panel shell, with an Identity screen inside it.</summary>
    public const string SignInPath = "/Identity/Account/Login";

    /// <summary>Where the switch posts. Not a constant of the app's — the point is that this address answers.</summary>
    public const string SwitchPath = "/Culture/Set";

    /// <summary>
    /// A signed-in page that renders the whole sidebar and reads nothing out of the database:
    /// <c>[Authorize]</c>, and its view is four lines. The sign-in page shows the shell for an
    /// anonymous caller; this shows it for somebody who is in.
    /// </summary>
    public const string SignedInPath = "/Identity/Account/Logout";

    /// <summary><c>operator</c>, <c>tenant</c>, or absent for an anonymous request.</summary>
    public const string RoleHeader = "X-Test-Role";

    private readonly SqliteConnection connection = OpenSchema();

    /// <summary>
    /// No cookie jar by default, so a test says on the request exactly what it means to say. The
    /// switch's own test opts in, because a cookie written by one response and read by the next is
    /// the whole behaviour under test there.
    /// </summary>
    public HttpClient NewClient(bool keepCookies = false) => CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = keepCookies,
        });

    /// <summary>
    /// The shell, with its character references resolved.
    ///
    /// Razor's default encoder escapes everything outside Basic Latin, so «ورود» written by an
    /// expression arrives as <c>&amp;#x648;…</c> — an assertion against the literal word would pass
    /// on a page that says it and on a page that does not. Every assertion in this folder reads the
    /// decoded text.
    /// </summary>
    public static async Task<string> TextAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The shell as a signed-in caller sees it, which is the only way to see the sidebar's own
    /// controls — the nav, the quota card and the sign-out form.
    /// </summary>
    /// <param name="role"><c>operator</c> or <c>tenant</c>; the two halves of the panel.</param>
    public async Task<string> SignedInShellAsync(string role, string? acceptLanguage = null)
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SignedInPath);

        request.Headers.Add(RoleHeader, role);
        if (acceptLanguage is not null) request.Headers.Add("Accept-Language", acceptLanguage);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await TextAsync(response);
    }

    /// <summary>
    /// The sign-in page, as a browser configured exactly this way would receive it.
    /// </summary>
    /// <param name="acceptLanguage">Null sends no header at all, which is what a bare request looks like.</param>
    /// <param name="cultureCookie">A language tag to arrive holding, in the framework's own cookie format.</param>
    /// <param name="query">Appended to the path — <c>?lang=en</c> and the like.</param>
    public async Task<string> ShellAsync(
        string? acceptLanguage = null,
        string? cultureCookie = null,
        string query = "")
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SignInPath + query);

        if (acceptLanguage is not null) request.Headers.Add("Accept-Language", acceptLanguage);
        if (cultureCookie is not null) request.Headers.Add("Cookie", CultureCookie(cultureCookie));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await TextAsync(response);
    }

    /// <summary>
    /// The cookie the switch writes, built by the framework rather than transcribed: the value shape
    /// (<c>c=fa|uic=fa</c>) and the URL escaping are both the framework's, and a hand-written copy of
    /// either would make these tests agree with themselves instead of with the middleware.
    /// </summary>
    public static string CultureCookie(string tag) =>
        $"{CultureCookieName}={UrlEncoder.Default.Encode(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(tag)))}";

    public static string CultureCookieName => CookieRequestCultureProvider.DefaultCookieName;

    /// <summary>
    /// The token the shell renders, fetched the way a browser gets it — by loading the page the
    /// switch is on. The client must be keeping cookies, or the matching antiforgery cookie is lost
    /// and the POST is refused for the wrong reason.
    /// </summary>
    public static async Task<string> TokenAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(new Uri(SignInPath, UriKind.Relative));

        return AntiforgeryToken(await TextAsync(response));
    }

    /// <summary>The antiforgery token in a page that has already been read.</summary>
    public static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The shell rendered no antiforgery token, so the switch cannot be posted.");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// An operator that is simply already there, so the sign-in address renders the sign-in form
    /// rather than the first-run setup screen. Both wear the shell; only one of them is the page a
    /// returning customer meets.
    /// </summary>
    public void SeedOperator(string email = "seeded-operator@driveunion.test")
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            IsOperator = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

        db.Users.Add(user);
        db.SaveChanges();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has already read the connection string on its second line.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");

        builder.ConfigureTestServices(services =>
        {
            // Before anything else, and before the connection below exists to be raced with.
            services.RemoveEveryBackgroundLoop();

            // AddDbContext leaves the context, DbContextOptions, DbContextOptions<T> and EF 9's
            // IDbContextOptionsConfiguration<T> behind, and the last still carries UseNpgsql —
            // stacking UseSqlite on a surviving provider throws at resolve time.
            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));

            // Cookie authentication swapped for a header-driven scheme, the way PanelPageHarness
            // does it: what is under test is what the Razor renders for each half of the panel, not
            // Identity, which has its own tests. A request without the header stays anonymous, so
            // one harness answers all three cases.
            //
            // All three defaults are named. AddIdentity has already pointed authenticate and
            // challenge at the application cookie, and setting only DefaultScheme would leave the
            // panel authenticating against a cookie no test can mint.
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = RoleHeaderAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = RoleHeaderAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = RoleHeaderAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthHandler>(
                    RoleHeaderAuthHandler.SchemeName, _ => { });

            // Booting reads this machine's user-secrets in some configurations, and a developer here
            // may well have a seeded operator set. Whether the sign-in page or the first-run screen
            // renders must not depend on whose machine the suite is on.
            services.Configure<DriveUnionSeedOptions>(options =>
            {
                options.OperatorEmail = null;
                options.OperatorPassword = null;
                options.TenantSlug = null;
                options.TenantUserEmail = null;
                options.TenantUserPassword = null;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }

    private static SqliteConnection OpenSchema()
    {
        // A :memory: database belongs to its connection, so this one is held open for the harness's
        // life and the schema is created before the app boots.
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

        // Includes DataProtectionKeys — the antiforgery token on the language switch is protected
        // with that key ring, so a missing table is a 500 on every page in the panel.
        schema.Database.EnsureCreated();

        return connection;
    }

    /// <summary>
    /// The claims the cookie would carry, minted from a header instead — the operator claim
    /// <c>DriveUnionPolicies.Operator</c> authorises on, or a tenant claim and nothing else, which
    /// is what the sidebar hides the whole Google pool behind.
    /// </summary>
    private sealed class RoleHeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RoleHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].ToString();

            List<Claim> claims = role switch
            {
                "operator" =>
                [
                    new Claim(ClaimTypes.Name, "operator@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue),
                ],
                "tenant" =>
                [
                    new Claim(ClaimTypes.Name, "reza@acme.example"),
                    new Claim(DriveUnionClaimTypes.TenantId, Guid.CreateVersion7().ToString()),
                ],
                _ => [],
            };

            if (claims.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(claims, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

/// <summary>
/// Sets the UI culture for the duration of a block and puts it back.
///
/// Culture flows with the execution context in .NET, so this does not leak into a test running
/// beside it — which matters, because these tests run in parallel and half of them are about what a
/// thread with the wrong culture on it renders.
/// </summary>
public sealed class CultureScope : IDisposable
{
    private readonly System.Globalization.CultureInfo previous;

    public CultureScope(System.Globalization.CultureInfo culture)
    {
        previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
    }

    public static CultureScope Persian() => new(PanelCulture.Persian);

    public static CultureScope English() => new(PanelCulture.English);

    public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = previous;
}
