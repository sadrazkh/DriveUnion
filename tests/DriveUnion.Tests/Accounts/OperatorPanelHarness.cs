using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// DriveUnion.Web's real pipeline with an operator already signed in, so the consent flow can be
/// walked over HTTP without a browser and without Google.
///
/// The unit tests beside this one settle what the controller decides. This settles the two things a
/// controller call cannot show: that the hidden <c>popup</c> field the accounts page renders is the
/// one the action binds — a name that drifts by one character would silently take the popup away
/// with nothing failing — and that the callback's closing page is a real rendered Razor view that
/// names an explicit target origin.
///
/// Google is configured with credentials that go nowhere. Every test here stops at the redirect to
/// the consent screen or comes back from it with an error; nothing reaches Google, which is the rule
/// this suite is built around.
/// </summary>
public sealed class OperatorPanelHarness : WebApplicationFactory<Program>
{
    public const string ClientId = "connect-test.apps.googleusercontent.com";
    public const string RedirectUri = "https://panel.example.test/accounts/callback";
    public const string StateCookie = "du_google_oauth_state";

    private const string TestScheme = "DriveUnion.TestOperator";

    private readonly SqliteConnection _connection;

    public OperatorPanelHarness(bool googleConfigured = true)
    {
        GoogleConfigured = googleConfigured;

        // A :memory: database belongs to its connection; held open for the harness's life, and
        // created here so the schema exists before the seeder runs at boot.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var schema = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);
        schema.Database.EnsureCreated();
    }

    /// <summary>False leaves <c>Google:*</c> unset, which is this machine and any fresh deployment.</summary>
    public bool GoogleConfigured { get; }

    /// <summary>
    /// Keeps a cookie jar and follows nothing. The jar is the point: the antiforgery cookie, the
    /// OAuth state cookie and the response's redirect are one conversation, and a handler that
    /// dropped cookies would make the state check unobservable.
    /// </summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>The token the accounts page renders, fetched the way the operator's browser gets it.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/accounts");

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The accounts page rendered no antiforgery token.");

        return match.Groups[1].Value;
    }

    /// <summary>The state cookie this response sets, or null when it sets none worth keeping.</summary>
    public static string? IssuedState(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return null;

        return Microsoft.Net.Http.Headers.SetCookieHeaderValue
            .ParseList([.. headers])
            .Where(c => c.Name == StateCookie && c.Value.Length > 0)
            .Select(c => c.Value.ToString())
            .LastOrDefault();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: Program.cs reads the connection string
        // while the host is still being built. The registration it produces is replaced below.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");

        if (GoogleConfigured)
        {
            builder.UseSetting("Google:ClientId", ClientId);
            builder.UseSetting("Google:ClientSecret", "not-a-real-secret");
            builder.UseSetting("Google:RedirectUri", RedirectUri);
        }

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));

        builder.ConfigureTestServices(services =>
        {
            ReplaceNpgsqlWithSqlite(services);

            // No IDriveClient may reach Google. Nothing in this file exchanges a code, and this is
            // what makes that a property of the harness rather than of the tests written against it.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IDriveClient)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IDriveClient, RefusingDriveClient>();

            // Identity's cookie is the app's default scheme; this replaces it wholesale rather than
            // forging a signed cookie, because what these tests need is an operator principal and
            // the real DriveUnionPolicies.Operator judging it.
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                    options.DefaultScheme = TestScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestOperatorHandler>(TestScheme, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) _connection.Dispose();
    }

    private void ReplaceNpgsqlWithSqlite(IServiceCollection services)
    {
        // AddDbContext leaves the context, DbContextOptions, DbContextOptions<T> and an
        // IDbContextOptionsConfiguration<T> that still carries UseNpgsql. Adding UseSqlite on top of
        // a surviving provider registration throws at resolve time, so everything naming this
        // context goes first.
        var doomed = services
            .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType
                    && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
            .ToList();

        foreach (var descriptor in doomed) services.Remove(descriptor);

        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(_connection));
    }

    /// <summary>An operator, on every request, with the claim the real policy authorises on.</summary>
    private sealed class TestOperatorHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "operator@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue),
                ],
                TestScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    /// <summary>Fails loudly rather than quietly: no test in this file has any business here.</summary>
    private sealed class RefusingDriveClient : IDriveClient
    {
        private static InvalidOperationException Refuse() =>
            new("No test in the accounts lane may talk to Drive.");

        public Task<DriveResumableSession> BeginResumableUploadAsync(
            Guid accountId,
            DriveUploadRequest request,
            CancellationToken cancellationToken) => throw Refuse();

        public Task<DriveChunkOutcome> WriteChunkAsync(
            Uri sessionUri,
            Stream content,
            long offset,
            long length,
            long totalSize,
            CancellationToken cancellationToken) => throw Refuse();

        public Task<long> GetConfirmedLengthAsync(
            Uri sessionUri,
            long totalSize,
            CancellationToken cancellationToken) => throw Refuse();

        public Task<DriveDownload> OpenDownloadAsync(
            Guid accountId,
            string driveFileId,
            string? rangeHeader,
            CancellationToken cancellationToken) => throw Refuse();

        public Task<string> EnsureFolderAsync(
            Guid accountId,
            string folderName,
            string? parentFolderId,
            CancellationToken cancellationToken) => throw Refuse();

        public Task<DriveStorageQuota> GetStorageQuotaAsync(
            Guid accountId,
            CancellationToken cancellationToken) => throw Refuse();
    }
}
