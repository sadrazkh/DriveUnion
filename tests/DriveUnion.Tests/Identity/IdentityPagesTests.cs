using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Tests.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// Half of "the panel is unreachable" was that the challenge went to a page nobody had written.
/// This boots the real pipeline and asks for the sign-in address, because area routing and area
/// view resolution are conventions — <c>/Views/Shared/{0}.cshtml</c> being the last area view
/// location is what lets these pages use the panel's own layout — and a convention that is nearly
/// right renders a 404 that reads like a missing controller.
/// </summary>
public class IdentityPagesTests
{
    [Fact]
    public async Task The_sign_in_page_is_served_at_the_address_the_cookie_handler_points_at()
    {
        using var harness = new IdentityPagesHarness();

        // With no operator at all this address answers with the first-run setup screen instead —
        // see FirstRunSetupTests. What is under test here is the sign-in form, so there is somebody
        // to sign in as.
        harness.SeedOperator();

        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri("/Identity/Account/Login", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("name=\"Email\"");
        html.Should().Contain("name=\"Password\"");

        // The shared layout, not a second one: the brand mark only exists in Views/Shared/_Layout.
        html.Should().Contain("brand-mark");

        // M1 has no sign-up (spec §12), so the page must not offer one.
        html.Should().NotContain("/Identity/Account/Register");

        // Nor the first-run screen, whose second password box is the thing that tells them apart.
        html.Should().NotContain("name=\"ConfirmPassword\"");
    }

    [Fact]
    public async Task Signing_out_is_a_form_and_not_a_link()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        // Anonymous, so the sign-out page is behind the same authentication everything else is.
        // Without ConfigureApplicationCookie it challenges to Identity's default path; either way
        // it must not be a page that ends a session for whoever happens to load an image.
        using var response = await client.GetAsync(new Uri("/Identity/Account/Logout", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}

/// <summary>
/// DriveUnion.Web's real pipeline with the Npgsql context swapped for SQLite in memory. Nothing
/// here reaches Google or Postgres; the connection string exists only because Program.cs refuses to
/// start without one.
/// </summary>
/// <param name="environment">
/// The hosting environment the app boots as. Only the first-run screen's generated-password offer
/// reads it, and it must be proved on both sides of that line.
/// </param>
public sealed class IdentityPagesHarness(string environment = "Production") : WebApplicationFactory<Program>
{
    /// <summary>The address the cookie handler points at, and the form's own action.</summary>
    public const string LoginPath = "/Identity/Account/Login";

    /// <summary>Over Identity's ten-character minimum, and not a credential to anything.</summary>
    public const string Password = "Correct-Horse-9!";

    private readonly SqliteConnection connection = OpenSchema();

    public HttpClient NewClient(bool keepCookies = false) => CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,

            // The antiforgery cookie, the session cookie and the redirect that follows a successful
            // POST are one conversation; a handler that drops cookies cannot walk it.
            HandleCookies = keepCookies,
        });

    /// <summary>The token a page renders, fetched the way a browser gets it.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative));

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"{path} rendered no antiforgery token.");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// An operator that is simply already there — a random id and no password, the way a row left by
    /// a configured seed or by an earlier deployment looks. Deliberately not
    /// <see cref="FirstOperator.SlotId"/>: the setup route is gated on there being an operator at
    /// all, not on that one particular key being filled.
    /// </summary>
    /// <summary>
    /// An operator with a real password hash, made through the real user manager.
    ///
    /// <para><see cref="SeedOperator"/> writes a row and no credential, which is everything the
    /// page tests need and nothing a sign-in can use. Anything that is about what the sign-in form
    /// <i>does</i> — the cookie it writes, how long it lasts — has to walk the whole form, and that
    /// needs a password somebody could actually type.</para>
    /// </summary>
    public async Task<AppUser> CreateOperatorWithPasswordAsync(
        string email = "operator-with-a-password@driveunion.test")
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            IsOperator = true,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>
    /// Signs in the way a person does: fetch the form, take its token, post what the form posts.
    ///
    /// <para>A 302 means the credentials were accepted; a 200 means the form came back with a
    /// message on it.</para>
    /// </summary>
    /// <param name="rememberMe">
    /// Whether the checkbox was left ticked. What goes on the wire is deliberately what a browser
    /// would send and not a tidy <c>RememberMe=true|false</c>: an unticked checkbox posts nothing at
    /// all, so the form carries a hidden <c>false</c> underneath it and a ticked box is a second
    /// value in front of that one. Posting the tidy version instead would pass against a form that
    /// had lost the hidden field — which is the bug this is here to catch.
    /// </param>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string email,
        string password,
        bool rememberMe)
    {
        ArgumentNullException.ThrowIfNull(client);

        var token = await AntiforgeryTokenAsync(client, LoginPath);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Email", email),
            new("Password", password),
        };

        if (rememberMe) fields.Add(new("RememberMe", "true"));

        fields.Add(new("RememberMe", "false"));

        return await client.PostAsync(
            new Uri(LoginPath, UriKind.Relative),
            new FormUrlEncodedContent(fields));
    }

    /// <summary>The panel cookie as the running app has it configured — name, life and all.</summary>
    public CookieAuthenticationOptions PanelCookie() => Services
        .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
        .Get(IdentityConstants.ApplicationScheme);

    /// <summary>
    /// The <c>Set-Cookie</c> the panel wrote on this response, parsed. Null when it wrote none,
    /// which is what a refused sign-in looks like.
    /// </summary>
    public SetCookieHeaderValue? PanelCookieOn(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var name = PanelCookie().Cookie.Name;

        return response.Headers.TryGetValues(HeaderNames.SetCookie, out var written)
            ? written.Select(header => SetCookieHeaderValue.Parse(header)).SingleOrDefault(c => c.Name == name)
            : null;
    }

    public AppUser SeedOperator(string email = "seeded-operator@driveunion.test")
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

        using var db = NewDbContext();
        db.Users.Add(user);
        db.SaveChanges();

        return user;
    }

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    public List<AppUser> AllUsers()
    {
        using var db = NewDbContext();

        return [.. db.Users.AsNoTracking()];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(environment);

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

            // Runs after Program.cs's Bind, so it wins. Booting as Development makes the host read
            // this machine's user-secrets, and the very key these tests are about — an operator to
            // seed — is one a developer here is likely to have set. Without this, whether the
            // first-run screen appears would depend on whose machine the suite is on.
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

        // Includes DataProtectionKeys — the antiforgery token on the sign-in form is protected with
        // that key ring, so a missing table is a 500 on the one page that has to work.
        schema.Database.EnsureCreated();

        return connection;
    }
}
