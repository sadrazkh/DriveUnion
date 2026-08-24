using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
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
    public const string ClientSecret = "not-a-real-secret";
    public const string RedirectUri = "https://panel.example.test/accounts/callback";
    public const string StateCookie = "du_google_oauth_state";

    /// <summary>
    /// The redirect URI this app derives from the address TestServer serves it on. The panel shows
    /// it as the value to register in Google Cloud when nothing else supplies one.
    /// </summary>
    public const string OriginRedirectUri = "http://localhost/accounts/callback";

    private const string TestScheme = "DriveUnion.TestOperator";

    private readonly SqliteConnection _connection;

    public OperatorPanelHarness(bool googleConfigured = true, bool isOperator = true)
    {
        GoogleConfigured = googleConfigured;
        IsOperator = isOperator;

        // Where the retired JSON credential store would have been. Its own path per harness, and
        // deliberately a file that does not exist: the one-time import walks straight back out, so
        // nothing here depends on a file the product no longer writes.
        CredentialStorePath = Path.Combine(
            Path.GetTempPath(),
            $"driveunion-oauth-{Guid.NewGuid():N}.json");

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
    /// False signs in a customer instead — authenticated, with a tenant, and without the one claim
    /// <c>DriveUnionPolicies.Operator</c> asks for. That is the caller every operator-only surface
    /// has to refuse.
    /// </summary>
    public bool IsOperator { get; }

    /// <summary>Where the credential file this product used to write would be, if it still did.</summary>
    public string CredentialStorePath { get; }

    /// <summary>
    /// The OAuth client rows exactly as they sit in the database.
    ///
    /// This is the assertion the file store could not support and the reason it was replaced: a
    /// redeploy destroyed the file while these rows — and the Data Protection key ring that reads
    /// them — survived, so every account had a refresh token nothing could refresh.
    /// </summary>
    public IReadOnlyList<GoogleOAuthClient> StoredClients()
    {
        using var db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);

        return [.. db.GoogleOAuthClients.AsNoTracking()];
    }

    /// <summary>
    /// Puts a pool in the database, the way a completed consent flow would leave one.
    ///
    /// Written straight to the table rather than through <c>GoogleAccountDirectory</c> because the
    /// directory's path to a row runs through Google, and no test in this lane may go there. What
    /// the screen reads is these columns; what the labels mean is settled next door in
    /// <c>GoogleAccountDirectoryTests</c>, over the real allocation.
    ///
    /// The protected token is a placeholder. Nothing on the accounts screen decrypts it, and a value
    /// that could be decrypted would be a secret sitting in a test fixture for no reason.
    ///
    /// <para><b>Call this before the first request.</b> It writes over its own context on the
    /// harness's connection rather than resolving one out of <c>Services</c>, which would boot the
    /// host — and every SQLite context that wraps an already-open connection registers user
    /// functions on it, which the driver refuses while another statement on that connection is
    /// live. Seeding before anything is running removes the question rather than timing it.</para>
    /// </summary>
    public IReadOnlyList<GoogleAccount> SeedPool(params (string Email, GoogleAccountStatus Status)[] accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        using var db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);

        var seeded = new List<GoogleAccount>();
        var connectedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < accounts.Length; index++)
        {
            var (email, status) = accounts[index];

            seeded.Add(new GoogleAccount
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                Label = $"A{index + 1}",
                RefreshTokenProtected = "not-a-real-protected-token",
                QuotaTotalBytes = 5497558138880,
                QuotaUsedBytes = 1099511627776,
                Status = status,

                // A minute apart, because the screen orders by this and two rows sharing an instant
                // would make the order of the cards a property of the database rather than a rule.
                CreatedAt = connectedAt.AddMinutes(index),
            });
        }

        db.GoogleAccounts.AddRange(seeded);
        db.SaveChanges();

        return seeded;
    }

    /// <summary>
    /// One account with the two columns the cards grew for the operator: the OAuth client it was
    /// connected under, and why it last stopped working. Same rules as <see cref="SeedPool"/> —
    /// straight to the table, before the first request.
    /// </summary>
    public GoogleAccount SeedAccount(
        string email,
        string label,
        string? oauthClientId = null,
        string? failureReason = null)
    {
        using var db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);

        var account = new GoogleAccount
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            Label = label,
            RefreshTokenProtected = "not-a-real-protected-token",
            QuotaTotalBytes = 5497558138880,
            QuotaUsedBytes = 1099511627776,
            OAuthClientId = oauthClientId,
            LastFailureReason = failureReason,
            LastFailureAt = failureReason is null
                ? null
                : new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.Zero),
            Status = failureReason is null ? GoogleAccountStatus.Healthy : GoogleAccountStatus.Disconnected,
            CreatedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        };

        db.GoogleAccounts.Add(account);
        db.SaveChanges();

        return account;
    }

    /// <summary>
    /// An OAuth client in the table, the way a save from the screen leaves one. The secret is a
    /// placeholder: nothing the accounts screen renders decrypts it, and a value that could be
    /// decrypted would be a secret sitting in a fixture for no reason.
    /// </summary>
    public GoogleOAuthClient SeedClient(string clientId, string label = "C1", bool isDefault = true)
    {
        using var db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);

        var client = new GoogleOAuthClient
        {
            Id = Guid.CreateVersion7(),
            Label = label,
            ClientId = clientId,
            ClientSecretProtected = "not-a-real-protected-secret",
            RedirectUri = OriginRedirectUri,
            IsDefault = isDefault,
            CreatedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        };

        db.GoogleOAuthClients.Add(client);
        db.SaveChanges();

        return client;
    }

    /// <summary>
    /// Saves an OAuth client the way the operator does: a form post from the accounts screen, with
    /// that screen's antiforgery token.
    /// </summary>
    /// <param name="id">
    /// The client being edited. Null adds one, which is what the «add» form on the screen posts.
    /// </param>
    public static async Task<HttpResponseMessage> SaveCredentialsAsync(
        HttpClient client,
        string clientId,
        string? clientSecret,
        string redirectUri,
        Guid? id = null)
    {
        var token = await AntiforgeryTokenAsync(client);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = token,
            ["ClientId"] = clientId,
            ["RedirectUri"] = redirectUri,
        };

        if (clientSecret is not null) fields["ClientSecret"] = clientSecret;
        if (id is { } editing) fields["Id"] = editing.ToString();

        return await client.PostAsync("/accounts/google-credentials", new FormUrlEncodedContent(fields));
    }

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

        // Always set, configured or not. It is only where the one-time import of the retired JSON
        // credential file looks; letting it default would point every test host at the App_Data
        // directory under the test project's content root.
        builder.UseSetting("Google:CredentialStorePath", CredentialStorePath);

        if (GoogleConfigured)
        {
            builder.UseSetting("Google:ClientId", ClientId);
            builder.UseSetting("Google:ClientSecret", ClientSecret);
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
            var isOperator = IsOperator;
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                    options.DefaultScheme = TestScheme;
                })
                .AddScheme<TestPrincipalOptions, TestPrincipalHandler>(
                    TestScheme,
                    options => options.IsOperator = isOperator);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _connection.Dispose();
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

    private sealed class TestPrincipalOptions : AuthenticationSchemeOptions
    {
        public bool IsOperator { get; set; } = true;
    }

    /// <summary>
    /// The signed-in caller, on every request: an operator with the claim the real policy authorises
    /// on, or — when the harness asks for one — an ordinary customer with a tenant and without it.
    ///
    /// The customer is authenticated on purpose. An anonymous request would be refused by
    /// <c>RequireAuthenticatedUser</c> and would prove nothing about the operator claim, which is
    /// the only thing standing between a paying customer and the operator's Google credentials.
    /// </summary>
    private sealed class TestPrincipalHandler(
        IOptionsMonitor<TestPrincipalOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<TestPrincipalOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            List<Claim> claims = Options.IsOperator
                ?
                [
                    new Claim(ClaimTypes.Name, "operator@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue),
                ]
                :
                [
                    new Claim(ClaimTypes.Name, "customer@driveunion.test"),
                    new Claim(DriveUnionClaimTypes.TenantId, Guid.CreateVersion7().ToString()),
                ];

            var identity = new ClaimsIdentity(claims, TestScheme);

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
