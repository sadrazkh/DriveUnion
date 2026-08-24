using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Controllers;
using DriveUnion.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public const string ClientSecret = "configured-secret";
    public const string RedirectUri = "https://panel.example.test/accounts/callback";

    /// <summary>
    /// The host these requests arrive on — deliberately not the host in <see cref="RedirectUri"/>.
    /// The screen derives a suggested redirect URI from the address it is being viewed at, and if
    /// the two were the same string no test could tell the derived one from the configured one.
    /// </summary>
    public const string RequestHost = "du.example.test";

    public const string SuggestedRedirectUri = $"https://{RequestHost}/accounts/callback";

    private ConnectFlowHarness(
        AccountsController controller,
        FakeAccountDirectory directory,
        FakeGoogleOAuthClientStore store,
        GoogleOAuthCredentialResolver credentials,
        FakeClientUsageReader usage)
    {
        Controller = controller;
        Directory = directory;
        Store = store;
        Credentials = credentials;
        Usage = usage;
    }

    public AccountsController Controller { get; }

    public FakeAccountDirectory Directory { get; }

    /// <summary>What the panel has saved, in memory.</summary>
    public FakeGoogleOAuthClientStore Store { get; }

    /// <summary>Which client each account belongs to, and why it last failed — supplied by hand.</summary>
    public FakeClientUsageReader Usage { get; }

    /// <summary>The real resolver, so these tests exercise the real precedence rule.</summary>
    public GoogleOAuthCredentialResolver Credentials { get; }

    public HttpContext Http => Controller.ControllerContext.HttpContext;

    public ITempDataDictionary TempData => Controller.TempData;

    /// <param name="configured">
    /// False supplies no <c>Google:*</c> configuration at all, which is the state of this machine
    /// and of every fresh deployment: nothing in the environment, nothing saved in the panel, and
    /// every path through the controller still has to answer.
    /// </param>
    public static ConnectFlowHarness Create(bool configured = true, params GoogleAccountSummary[] pool)
    {
        var directory = new FakeAccountDirectory(pool);
        var store = new FakeGoogleOAuthClientStore();
        var credentials = new GoogleOAuthCredentialResolver(BuildSection(configured), store);
        var usage = new FakeClientUsageReader();

        var http = new DefaultHttpContext();

        // The suggested redirect URI is built out of the request, so a request with no scheme or
        // host would make every assertion about it a statement about DefaultHttpContext.
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(RequestHost);

        var controller = new AccountsController(
            directory,
            credentials,
            usage,
            NullLogger<AccountsController>.Instance)
        {
            ControllerContext = new ControllerContext(
                new ActionContext(http, new RouteData(), new ControllerActionDescriptor())),
        };

        controller.TempData = new TempDataDictionary(http, new NoOpTempDataProvider());
        controller.ViewData = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            controller.ModelState);

        return new ConnectFlowHarness(controller, directory, store, credentials, usage);
    }

    /// <summary>The view model the accounts screen was rendered with.</summary>
    public static AccountsPageViewModel PageModel(IActionResult result) =>
        Assert.IsType<AccountsPageViewModel>(Assert.IsType<ViewResult>(result).Model);

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
    /// The <c>Google</c> section as a deployment supplies it. Real configuration rather than a
    /// hand-built options object, because "the environment outranks the panel" is a rule about this
    /// section and a stub would agree with whatever the test assumed.
    /// </summary>
    private static IConfiguration BuildSection(bool configured)
    {
        var settings = configured
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GoogleOAuthOptions.SectionName}:ClientId"] = ClientId,
                [$"{GoogleOAuthOptions.SectionName}:ClientSecret"] = ClientSecret,
                [$"{GoogleOAuthOptions.SectionName}:RedirectUri"] = RedirectUri,
            }
            : new Dictionary<string, string?>(StringComparer.Ordinal);

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(GoogleOAuthOptions.SectionName);
    }
}

/// <summary>
/// The panel's own copy of the OAuth clients, in memory.
///
/// It stands in for the database store on the controller lane only. Whether a secret survives
/// encryption and a restart is <c>GoogleOAuthClientStoreTests</c>'s question, over the real store,
/// a real database and the real Data Protection protector; this one is about what the controller
/// does with the answers.
/// </summary>
public sealed class FakeGoogleOAuthClientStore : IGoogleOAuthClientStore
{
    private readonly List<Row> _rows = [];

    public int SaveCalls { get; private set; }

    /// <summary>Set to make every stored secret undecryptable, which is a lost Data Protection key.</summary>
    public bool SecretIsUnreadable { get; set; }

    /// <summary>
    /// Which accounts a client is refused removal over, by label. Set by a test; the real store
    /// reads it off the accounts table.
    /// </summary>
    public Dictionary<string, string[]> DependentAccounts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The plaintext secret of the default client, as the store received it. Tests read it to prove
    /// the value that went in is the value that comes back out — and never to render it.
    /// </summary>
    public string? Secret => Default() is { } client ? ReadSecret(client.Id) : null;

    public IReadOnlyList<StoredGoogleOAuthClient> List() => [.. _rows.Select(Describe)];

    public StoredGoogleOAuthClient? Default()
    {
        var row = _rows.FirstOrDefault(r => r.IsDefault) ?? _rows.FirstOrDefault();

        return row is null ? null : Describe(row);
    }

    public StoredGoogleOAuthClient? Find(Guid id) =>
        _rows.FirstOrDefault(r => r.Id == id) is { } row ? Describe(row) : null;

    public StoredGoogleOAuthClient? FindByClientId(string clientId) =>
        _rows.FirstOrDefault(r => r.ClientId == clientId) is { } row ? Describe(row) : null;

    public string? ReadSecret(Guid id) =>
        SecretIsUnreadable ? null : _rows.FirstOrDefault(r => r.Id == id)?.Secret;

    public string? ReadSecretForClientId(string clientId) =>
        SecretIsUnreadable ? null : _rows.FirstOrDefault(r => r.ClientId == clientId)?.Secret;

    public GoogleOAuthClientSaveResult Save(
        Guid? id,
        string clientId,
        string? clientSecret,
        string redirectUri)
    {
        SaveCalls++;

        if (_rows.Any(r => r.Id != id && r.ClientId == clientId))
        {
            return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.DuplicateClientId, null);
        }

        Row row;

        if (id is { } existing)
        {
            if (_rows.FirstOrDefault(r => r.Id == existing) is not { } found)
            {
                return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.NotFound, null);
            }

            row = found;
        }
        else
        {
            row = new Row(Guid.CreateVersion7(), $"C{_rows.Count + 1}") { IsDefault = _rows.Count == 0 };
            _rows.Add(row);
        }

        row.ClientId = clientId;
        row.RedirectUri = redirectUri;
        row.Secret = string.IsNullOrEmpty(clientSecret) ? row.Secret : clientSecret;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.Saved, Describe(row));
    }

    public bool MakeDefault(Guid id)
    {
        if (_rows.All(r => r.Id != id)) return false;

        foreach (var row in _rows) row.IsDefault = row.Id == id;

        return true;
    }

    public GoogleOAuthClientRemovalResult Remove(Guid id)
    {
        if (_rows.FirstOrDefault(r => r.Id == id) is not { } row)
        {
            return new GoogleOAuthClientRemovalResult(GoogleOAuthClientRemoval.NotFound, []);
        }

        if (DependentAccounts.TryGetValue(row.ClientId, out var labels) && labels.Length > 0)
        {
            return new GoogleOAuthClientRemovalResult(
                GoogleOAuthClientRemoval.InUseByAccounts,
                labels);
        }

        _rows.Remove(row);

        if (row.IsDefault && _rows.Count > 0) _rows[0].IsDefault = true;

        return new GoogleOAuthClientRemovalResult(GoogleOAuthClientRemoval.Removed, []);
    }

    private StoredGoogleOAuthClient Describe(Row row) => new(
        row.Id,
        row.Label,
        row.ClientId,
        row.RedirectUri,
        row.Secret is not null && !SecretIsUnreadable,
        row.IsDefault,
        row.UpdatedAt);

    private sealed class Row(Guid id, string label)
    {
        public Guid Id { get; } = id;

        public string Label { get; } = label;

        public string ClientId { get; set; } = string.Empty;

        public string RedirectUri { get; set; } = string.Empty;

        public string? Secret { get; set; }

        public bool IsDefault { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}

/// <summary>
/// What the accounts screen is told about the clients, supplied by hand. The real reader joins the
/// accounts table to the stored clients; on this lane there is no database at all.
/// </summary>
public sealed class FakeClientUsageReader : IGoogleClientUsageReader
{
    public Dictionary<Guid, GoogleAccountClientNote> Accounts { get; } = [];

    public Dictionary<string, int> AccountsPerClientId { get; } = new(StringComparer.Ordinal);

    public Task<GoogleClientUsage> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GoogleClientUsage(Accounts, AccountsPerClientId));
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
