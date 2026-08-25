using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Tests.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// DriveUnion.Web's real pipeline with <b>real Identity cookies</b>, so a workspace can be created
/// on one screen and signed into on another.
///
/// <para>Every other panel harness in this suite forges a principal from a header, which is right for
/// what those tests are about and is exactly wrong for these. What is under test here is the whole
/// round trip: an operator creates an account, a human being takes the password they were given to
/// the sign-in form, and a cookie comes back that opens the customer's own files. A forged principal
/// skips the password hash, the security stamp and the lockout — which is to say it skips everything
/// this file is about.</para>
///
/// <para>It calls <see cref="TenancyServiceCollectionExtensions.AddDriveUnionTenancy"/>, which is the
/// one line <c>Program.cs</c> needs and which this work was not allowed to add there. What the tests
/// below prove is therefore exactly what that line does — including the security-stamp validation
/// interval, without which "disabled" would mean "disabled within half an hour".</para>
/// </summary>
public sealed class TenantPanelHarness : WebApplicationFactory<Program>
{
    public const string OperatorEmail = "ops@driveunion.test";

    /// <summary>Over Identity's ten-character minimum, and not a credential to anything.</summary>
    public const string Password = "Correct-Horse-9!";

    private readonly SqliteConnection connection = OpenSchema();

    /// <summary>
    /// A client with a cookie jar and no automatic redirects.
    ///
    /// <para>The jar is the point: the antiforgery cookie, the session cookie and the redirect that
    /// follows a successful POST are one conversation, and a handler that drops cookies cannot walk
    /// it. Following redirects is off so a challenge is visible as the 302 it is rather than as the
    /// sign-in page it lands on.</para>
    ///
    /// <para><c>Accept-Language: en</c> because these tests read sentences back out of the HTML.
    /// The header is the weakest of the three culture providers, so it decides only where nothing
    /// else has — which is every request here, since nothing touches the language switch.</para>
    /// </summary>
    public HttpClient NewClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        client.DefaultRequestHeaders.Add("Accept-Language", "en");

        return client;
    }

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

    /// <summary>
    /// The operator, created through the real user manager so it has a real password hash. Without
    /// one there is nobody to sign in as, and <c>/Identity/Account/Login</c> answers with the
    /// first-run setup screen instead of the sign-in form.
    /// </summary>
    public async Task<AppUser> CreateOperatorAsync(string email = OperatorEmail)
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
            TenantId = null,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>An operator's client, already signed in.</summary>
    public async Task<HttpClient> SignedInOperatorAsync()
    {
        await CreateOperatorAsync();

        var client = NewClient();
        var signedIn = await SignInAsync(client, OperatorEmail, Password);

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        return client;
    }

    /// <summary>
    /// Signs in the way a person does: fetch the form, take its token, post the pair.
    ///
    /// <para>A 302 means the credentials were accepted. A 200 means the form came back with a
    /// message on it, which is what a wrong password and a locked-out account both look like.</para>
    /// </summary>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string email,
        string password)
    {
        ArgumentNullException.ThrowIfNull(client);

        var token = await AntiforgeryTokenAsync(client, "/Identity/Account/Login");

        return await client.PostAsync(
            new Uri("/Identity/Account/Login", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = email,
                ["Password"] = password,
                ["RememberMe"] = "false",
            }));
    }

    /// <summary>The token a page renders, fetched the way a browser gets it.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var page = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync();

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"{path} rendered no antiforgery token.");

        return match.Groups[1].Value;
    }

    /// <summary>A form post carrying the token the given page rendered.</summary>
    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string tokenFrom,
        string path,
        Dictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fields);

        var body = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = await AntiforgeryTokenAsync(client, tokenFrom),
        };

        return await client.PostAsync(new Uri(path, UriKind.Relative), new FormUrlEncodedContent(body));
    }

    /// <summary>The workspace id a successful create redirected to.</summary>
    public static Guid TenantIdFrom(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var location = response.Headers.Location?.ToString() ?? string.Empty;

        var match = Regex.Match(
            location,
            "/operator/tenants/([0-9a-fA-F-]{36})",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"'{location}' is not a redirect to a workspace.");

        return Guid.Parse(match.Groups[1].Value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has already read the connection string on its second line.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("DriveUnion:PublicBaseUrl", "https://links.example.test");

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));

        builder.ConfigureTestServices(services =>
        {
            // AddDbContext leaves the context, DbContextOptions, DbContextOptions<T> and EF's
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

            // Every background loop, not only Telegram's. Naming one namespace left the trash purge
            // sweeper running here, and it opens scopes on the same shared connection for the same
            // reason — the version of this that listed the loops it knew about is exactly how the
            // next one got missed.
            services.RemoveEveryBackgroundLoop();

            // The one line Program.cs is missing. Written as the same call rather than as three
            // hand-rolled registrations, so a test that passes here is a test that passes there.
            services.AddDriveUnionTenancy();

            // Runs after Program.cs's Bind, so it wins. These tests create their own operator, and
            // a developer's user-secrets naming one would otherwise decide whether the sign-in page
            // or the first-run screen answers — which is to say, whose machine the suite is on.
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

        // EnsureCreated applies the model's seed data, so the plan catalogue is there — and it
        // includes DataProtectionKeys, without which the antiforgery token on the sign-in form is a
        // 500 on the one page that has to work.
        schema.Database.EnsureCreated();

        return connection;
    }
}

/// <summary>
/// The three classes that boot a whole web host share one collection, so at most one of them is
/// building a container and a SQLite schema at a time.
///
/// <para>xUnit's default is a collection per class, which would have three panels booting at once
/// beside everything else the suite is running. Nothing here is faster for it, and the timing-
/// sensitive tests elsewhere in the suite — the <c>/d/*</c> limiter spends a bucket of 120 in a loop
/// that has to finish inside the refill period — are measurably worse for it.</para>
/// </summary>
[CollectionDefinition(TenantHostCollection.Name)]
public sealed class TenantHostCollection
{
    public const string Name = "DriveUnion.Tenants.Host";
}
