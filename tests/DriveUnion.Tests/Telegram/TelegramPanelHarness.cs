using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Telegram;
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

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// DriveUnion.Web's real pipeline, with either an operator or a customer signed in, so the two
/// Telegram screens can be fetched over HTTP and read as the browser gets them.
///
/// The service-level tests beside this one settle what the flow decides. This settles the two things
/// a service call cannot show: that the operator's screen renders no customer identity even when
/// bindings exist, and that both halves of the controller are guarded by the policy they claim.
///
/// <para><b>The Telegram services are registered here rather than by Program.cs.</b> Program.cs is
/// not this slice's to edit; the one line it needs is in the report that came with this work, and
/// this harness adds the same registration so the screens can be exercised meanwhile.</para>
/// </summary>
public sealed class TelegramPanelHarness : WebApplicationFactory<Program>
{
    /// <summary>Shaped like a real @BotFather token so the controller's format check passes.</summary>
    public const string BotToken = "123456789:AAHharnessBotTokenValue";

    public const string BotUsername = "DriveUnionBot";

    private const string TestScheme = "DriveUnion.TestTelegram";

    private readonly SqliteConnection _connection;

    public TelegramPanelHarness(bool isOperator = true)
    {
        IsOperator = isOperator;
        UserId = Guid.NewGuid();
        TenantId = Guid.NewGuid();

        CredentialStorePath = Path.Combine(
            Path.GetTempPath(),
            $"driveunion-telegram-{Guid.NewGuid():N}.json");

        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var schema = NewDbContext();

        // Includes DataProtectionKeys: the antiforgery token on every panel page is protected with
        // that key ring, and a missing table is a 500 before any view runs.
        schema.Database.EnsureCreated();
    }

    /// <summary>False signs in a customer instead: authenticated, with a tenant, without the claim.</summary>
    public bool IsOperator { get; }

    /// <summary>The signed-in user's id, which is what the linking flow is keyed on.</summary>
    public Guid UserId { get; }

    public Guid TenantId { get; }

    /// <summary>Where the panel writes the Google client. Kept off the content root and removed on dispose.</summary>
    public string CredentialStorePath { get; }

    public DriveUnionDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(_connection).Options);

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>
    /// Configures the bot through the application's own store, so the token is protected by the same
    /// Data Protection key ring the running panel uses. Writing the column by hand would store a
    /// value that does not decrypt, and the read model reports that as no token at all — which is
    /// correct behaviour and would make every screen below render the unconfigured card.
    /// </summary>
    public async Task ConfigureBotAsync()
    {
        using var scope = Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<ITelegramBotSettingsStore>();

        await store.SaveAsync(BotToken, BotUsername, null, CancellationToken.None);
    }

    /// <summary>
    /// A page's markup with its character references resolved.
    ///
    /// Razor's default HTML encoder only passes Basic Latin through, so every Persian string that
    /// arrives from a view model — «ناقص», a customer's display name, a Persian digit — is written as
    /// <c>&amp;#x6F0;</c> rather than as itself. Asserting against the raw response would make a
    /// <c>Contain</c> fail for text that is on the page and, far worse, make a <c>NotContain</c> pass
    /// for text that leaked. Decoding first is what makes both assertions mean what they say.
    /// </summary>
    public static async Task<string> ReadTextAsync(HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        return WebUtility.HtmlDecode(await client.GetStringAsync(path));
    }

    public static async Task<string> ReadTextAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The antiforgery token a page renders, fetched the way the browser gets it.</summary>
    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);

        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*?value=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"{path} rendered no antiforgery token.");

        return match.Groups[1].Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: Program.cs reads the connection string
        // while the host is still being built. The registration it produces is replaced below.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");
        builder.UseSetting("Google:CredentialStorePath", CredentialStorePath);

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));

        builder.ConfigureTestServices(services =>
        {
            ReplaceNpgsqlWithSqlite(services);

            // The one line Program.cs is missing. It has to come after AddGoogleDrive, which is what
            // registers ITokenProtector — the bot token is encrypted with the same key ring as the
            // Google refresh tokens, and by this point that registration has already happened.
            services.AddDriveUnionTelegram();

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IDriveClient)).ToList())
            {
                services.Remove(descriptor);
            }

            var isOperator = IsOperator;
            var userId = UserId;
            var tenantId = TenantId;

            // Identity's cookie is the app's default scheme; this replaces it wholesale rather than
            // forging a signed cookie, because what these tests need is a principal and the real
            // DriveUnionPolicies judging it.
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                    options.DefaultScheme = TestScheme;
                })
                .AddScheme<TestPrincipalOptions, TestPrincipalHandler>(
                    TestScheme,
                    options =>
                    {
                        options.IsOperator = isOperator;
                        options.UserId = userId;
                        options.TenantId = tenantId;
                    });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _connection.Dispose();

        if (File.Exists(CredentialStorePath)) File.Delete(CredentialStorePath);
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

        public Guid UserId { get; set; }

        public Guid TenantId { get; set; }
    }

    /// <summary>
    /// The signed-in caller: an operator with the claim the real policy authorises on, or a customer
    /// with a tenant and without it.
    ///
    /// The customer is authenticated on purpose. An anonymous request would be refused by
    /// <c>RequireAuthenticatedUser</c> and would prove nothing about the operator claim, which is the
    /// only thing standing between a paying customer and the bot token.
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
                    new Claim(ClaimTypes.NameIdentifier, Options.UserId.ToString()),
                    new Claim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue),
                ]
                :
                [
                    new Claim(ClaimTypes.Name, "customer@driveunion.test"),
                    new Claim(ClaimTypes.NameIdentifier, Options.UserId.ToString()),
                    new Claim(DriveUnionClaimTypes.TenantId, Options.TenantId.ToString()),
                ];

            var identity = new ClaimsIdentity(claims, TestScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
