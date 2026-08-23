using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// One <see cref="AccountsController"/> over a fake pool, and the two cookies the consent flow is
/// made of.
///
/// A controller with a <see cref="DefaultHttpContext"/> rather than the whole pipeline: everything
/// this suite is about — the state cookie's shape, the fixed-time comparison, and which of the two
/// responses the callback picks — happens between reading a cookie and returning an
/// <see cref="IActionResult"/>. Booting Kestrel to observe that would also mean standing up an
/// operator session, and would still not reach Google, which no test may do.
/// </summary>
public sealed class ConnectFlowHarness
{
    public const string StateCookie = "du_google_oauth_state";

    /// <summary>The only three settings that make <see cref="GoogleOAuthOptions"/> configured.</summary>
    public const string ClientId = "client-id.apps.googleusercontent.com";
    public const string RedirectUri = "https://panel.example.test/accounts/callback";

    private ConnectFlowHarness(AccountsController controller, FakeAccountDirectory directory)
    {
        Controller = controller;
        Directory = directory;
    }

    public AccountsController Controller { get; }

    public FakeAccountDirectory Directory { get; }

    public HttpContext Http => Controller.ControllerContext.HttpContext;

    public ITempDataDictionary TempData => Controller.TempData;

    /// <param name="configured">
    /// False leaves the options failing their real <c>Validate</c>, which is the state of this
    /// machine: no Google credentials, and every path through the controller still has to answer.
    /// </param>
    public static ConnectFlowHarness Create(bool configured = true, params GoogleAccountSummary[] pool)
    {
        var directory = new FakeAccountDirectory(pool);

        var http = new DefaultHttpContext();
        var controller = new AccountsController(
            directory,
            BuildOptions(configured),
            NullLogger<AccountsController>.Instance)
        {
            ControllerContext = new ControllerContext(
                new ActionContext(http, new RouteData(), new ControllerActionDescriptor())),
        };

        controller.TempData = new TempDataDictionary(http, new NoOpTempDataProvider());
        controller.ViewData = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            controller.ModelState);

        return new ConnectFlowHarness(controller, directory);
    }

    /// <summary>Sends the next request in with the state cookie a browser would still be holding.</summary>
    public void SendStateCookie(string value) =>
        Http.Request.Headers.Cookie = $"{StateCookie}={value}";

    /// <summary>The state cookie this response sets, or null when it sets none.</summary>
    public string? IssuedState() => IssuedCookie()?.Value.ToString();

    public SetCookieHeaderValue? IssuedCookie() =>
        SetCookieHeaderValue
            .ParseList(Http.Response.Headers.SetCookie)
            .LastOrDefault(c => c.Name == StateCookie && c.Value.Length > 0);

    /// <summary>
    /// Options built through the real <c>AddOptions().Validate()</c> chain, so an unconfigured client
    /// throws the same <see cref="OptionsValidationException"/> from <c>.Value</c> that the running
    /// panel throws — the exception the accounts screen is written to survive.
    /// </summary>
    private static IOptions<GoogleOAuthOptions> BuildOptions(bool configured)
    {
        var services = new ServiceCollection();
        var builder = services.AddOptions<GoogleOAuthOptions>().Validate(o => o.IsConfigured(), "unset");

        if (configured)
        {
            builder.Configure(o =>
            {
                o.ClientId = ClientId;
                o.ClientSecret = "secret";
                o.RedirectUri = RedirectUri;
            });
        }

        return services.BuildServiceProvider().GetRequiredService<IOptions<GoogleOAuthOptions>>();
    }
}

/// <summary>The operator's pool, in memory, with no Google behind it.</summary>
public sealed class FakeAccountDirectory(params GoogleAccountSummary[] accounts) : IGoogleAccountDirectory
{
    private readonly List<GoogleAccountSummary> _accounts = [.. accounts];

    /// <summary>Set to make the code exchange fail the way an unreachable Google fails.</summary>
    public DriveApiException? ConnectFailure { get; set; }

    public string? ExchangedCode { get; private set; }

    public string? ExchangedRedirectUri { get; private set; }

    public int ConnectCalls { get; private set; }

    public Task<IReadOnlyList<GoogleAccountSummary>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GoogleAccountSummary>>(_accounts);

    public Task<Guid> ConnectAsync(string authorizationCode, string redirectUri, CancellationToken cancellationToken)
    {
        ConnectCalls++;
        ExchangedCode = authorizationCode;
        ExchangedRedirectUri = redirectUri;

        if (ConnectFailure is not null) throw ConnectFailure;

        var id = Guid.NewGuid();
        _accounts.Add(new GoogleAccountSummary(
            id,
            "pool@gmail.com",
            "A1",
            GoogleAccountStatus.Healthy,
            5L * 1024 * 1024 * 1024 * 1024,
            0,
            DateTimeOffset.UtcNow));

        return Task.FromResult(id);
    }

    public Task<bool> DisconnectAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_accounts.RemoveAll(a => a.Id == accountId) > 0);

    public Task RefreshQuotaAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// TempData that lives for one controller call. The real provider writes a cookie, which would put
/// a second Set-Cookie in front of the one these tests read.
/// </summary>
public sealed class NoOpTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) =>
        new Dictionary<string, object>(StringComparer.Ordinal);

    public void SaveTempData(HttpContext context, IDictionary<string, object> values)
    {
    }
}
