using System.Net;
using System.Text;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The refresh, the lock around it, and the client it is run with.
///
/// The last of those is the one that is easy to get wrong and expensive to get wrong. Google binds a
/// refresh token to the client that obtained it; presenting it under another client id is
/// <c>invalid_grant</c>, which is the same answer as a revoked token — so a panel holding two
/// clients that refreshed with "whichever is in force" would disconnect accounts an hour after
/// connecting them and blame the consent screen. Multi-client support looks like it works right up
/// until that hour elapses, which is why it is pinned here rather than left to the screen.
///
/// SQLite stands in for Postgres — there is no reachable development database on this machine and
/// none of what is being asserted is dialect-specific.
/// </summary>
public sealed class GoogleTokenServiceTests : IDisposable
{
    private const string RefreshedToken = "ya29.refreshed-access-token";

    private const string ConfiguredClientId = "client-id.apps.googleusercontent.com";
    private const string ConfiguredSecret = "client-secret";

    private static readonly Guid AccountId = Guid.Parse("6b1a5d2c-4f30-4a91-9a2e-7c8d5e1f0a3b");

    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private readonly DataProtectionTokenProtector _protector;
    private readonly ImmediateTimeProvider _time = new();

    public GoogleTokenServiceTests()
    {
        // A named shared-cache in-memory database, kept alive by one open connection, so that every
        // scope the service opens gets its own connection to the same data — which is the arrangement
        // the concurrency test below actually needs.
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();

        _protector = new DataProtectionTokenProtector(
            new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionTokenProtector>.Instance);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        db.Database.EnsureCreated();

        db.GoogleAccounts.Add(new GoogleAccount
        {
            Id = AccountId,
            Email = "pool-a1@example.com",
            Label = "A1",
            RefreshTokenProtected = _protector.Protect("1//stored-refresh-token"),
            Status = GoogleAccountStatus.Healthy,
            CreatedAt = _time.GetUtcNow(),
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }

    [Fact]
    public async Task Twenty_concurrent_callers_produce_one_call_to_Google()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.GetAccessTokenAsync(AccountId, CancellationToken.None)))
            .ToArray();

        var tokens = await Task.WhenAll(callers);

        tokens.Should().AllBe(RefreshedToken);

        // The whole point of the gate. Twenty chunk uploads starting at once must not become twenty
        // refreshes — Google would throttle them, and nineteen of the tokens would be thrown away.
        stub.CallCount.Should().Be(1);
        stub.LastRequest.Body.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_refreshed_token_is_persisted_encrypted_with_its_expiry()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        var account = await AccountAsync();

        account.AccessTokenProtected.Should().NotBeNull();
        account.AccessTokenProtected.Should().NotBe(RefreshedToken, "a database dump is not a key ring");
        _protector.Unprotect(account.AccessTokenProtected!).Should().Be(RefreshedToken);
        account.AccessTokenExpiresAt.Should().Be(_time.GetUtcNow().AddSeconds(3599));
    }

    [Fact]
    public async Task A_cached_token_that_has_not_expired_does_not_go_to_Google_at_all()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        await service.GetAccessTokenAsync(AccountId, CancellationToken.None);
        var second = await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        second.Should().Be(RefreshedToken);
        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_grant_disconnects_the_account_instead_of_failing_obscurely()
    {
        var stub = new StubHttpMessageHandler((_, _) => StubResponses.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        var service = Build(stub);

        var act = async () => await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        // Seven days is the tell: while the consent screen sits in Testing publishing status, Google
        // expires refresh tokens issued to external users after a week.
        await act.Should().ThrowAsync<DriveAccountUnavailableException>().WithMessage("*seven days*");

        var account = await AccountAsync();

        account.Status.Should().Be(GoogleAccountStatus.Disconnected);

        // And the operator is told why. Status alone said «قطع شده» and nothing else, so a pool that
        // died because a redeploy deleted its OAuth client looked exactly like one whose consent
        // screen had expired — and both looked like nothing.
        account.LastFailureReason.Should().Contain("Token has been expired or revoked");
        account.LastFailureAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task An_account_that_is_not_in_the_pool_is_named_as_such()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        var act = async () => await service.GetAccessTokenAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<DriveAccountUnavailableException>().WithMessage("*not in the pool*");
        stub.CallCount.Should().Be(0);
    }

    /// <summary>
    /// A deployment with no credentials at all. The account is told about it — that is the whole
    /// point of the failure column — but it is not disconnected: nothing is wrong with it, and
    /// disconnecting would make the operator reconnect every account after fixing something that was
    /// never the account's fault.
    /// </summary>
    [Fact]
    public async Task Missing_configuration_says_which_keys_are_missing_without_disconnecting_anything()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub, configured: false);

        var act = async () => await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        await act.Should().ThrowAsync<DriveAccountUnavailableException>()
            .WithMessage("*Google:ClientId*");

        var account = await AccountAsync();
        account.Status.Should().Be(GoogleAccountStatus.Healthy);
        account.LastFailureReason.Should().Contain("Google:ClientId");
    }

    // ─────────────────────────────────────────── the client a refresh token belongs to

    /// <summary>
    /// The one that matters. Two clients, an account connected under the second, and the client in
    /// force is the first — so a refresh that asked "what is in force" would present the wrong
    /// credential and Google would answer <c>invalid_grant</c>, which this product reports as an
    /// account the operator has to reconnect. It would happen an hour after connecting, to whichever
    /// accounts were not on the default client, and nothing on any screen would say why.
    /// </summary>
    [Fact]
    public async Task An_account_is_refreshed_with_the_client_that_connected_it_and_not_the_one_in_force()
    {
        var store = Store();
        store.Save(id: null, "first.apps.googleusercontent.com", "GOCSPX-first", Redirect);
        store.Save(id: null, "second.apps.googleusercontent.com", "GOCSPX-second", Redirect);

        await BindAccountAsync("second.apps.googleusercontent.com");

        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub, configured: false);

        await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        var form = Form(stub.LastRequest.Body);

        form["client_id"].Should().Be("second.apps.googleusercontent.com");
        form["client_secret"].Should().Be("GOCSPX-second");
        form["grant_type"].Should().Be("refresh_token");
    }

    /// <summary>
    /// The failure this whole change is about, reported instead of hidden. The client that connected
    /// the account is gone — deleted from the panel, or destroyed with the container the JSON file
    /// store used to live in — and no other client can refresh it. Google is not even asked.
    /// </summary>
    [Fact]
    public async Task An_account_whose_client_is_gone_is_disconnected_and_the_card_is_told_why()
    {
        await BindAccountAsync("deleted-last-deploy.apps.googleusercontent.com");

        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub, configured: false);

        var act = async () => await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        await act.Should().ThrowAsync<DriveAccountUnavailableException>()
            .WithMessage("*deleted-last-deploy.apps.googleusercontent.com*");

        stub.CallCount.Should().Be(0, "presenting the token under another client is invalid_grant, "
            + "which is indistinguishable from a revoked account and would look like the operator's fault");

        var account = await AccountAsync();
        account.Status.Should().Be(GoogleAccountStatus.Disconnected);
        account.LastFailureReason.Should().Contain("deleted-last-deploy.apps.googleusercontent.com");
    }

    /// <summary>
    /// A row written before accounts were bound to a client at all. There was only one client then,
    /// so the client in force is the client that connected it — and the answer is written onto the
    /// row the first time it works, so the fallback retires itself.
    /// </summary>
    [Fact]
    public async Task An_unbound_account_is_refreshed_with_the_client_in_force_and_stamped_with_it()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        Form(stub.LastRequest.Body)["client_id"].Should().Be(ConfiguredClientId);

        (await AccountAsync()).OAuthClientId.Should().Be(ConfiguredClientId);
    }

    /// <summary>
    /// An account connected under the client a deployment supplies from its environment has no
    /// stored row to resolve, and still has to be refreshable.
    /// </summary>
    [Fact]
    public async Task The_configured_client_refreshes_the_accounts_bound_to_it()
    {
        await BindAccountAsync(ConfiguredClientId);

        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub);

        await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        Form(stub.LastRequest.Body)["client_secret"].Should().Be(ConfiguredSecret);
    }

    [Fact]
    public async Task A_refresh_that_works_clears_the_failure_the_card_was_showing()
    {
        await UpdateAccountAsync(account =>
        {
            account.LastFailureReason = "Google rejected the grant (invalid_grant).";
            account.LastFailureAt = _time.GetUtcNow();
        });

        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());

        await Build(stub).GetAccessTokenAsync(AccountId, CancellationToken.None);

        var account = await AccountAsync();
        account.LastFailureReason.Should().BeNull("chasing a fault that fixed itself is worse than no card at all");
        account.LastFailureAt.Should().BeNull();
    }

    /// <summary>
    /// A 500 from Google is not the account's fault and must not disconnect it — the next attempt
    /// may well work. It is still the only record the operator will ever get of why an upload
    /// failed, so it is written down.
    /// </summary>
    [Fact]
    public async Task A_transient_refusal_is_recorded_without_disconnecting_the_account()
    {
        var stub = new StubHttpMessageHandler((_, _) => StubResponses.Json(
            HttpStatusCode.InternalServerError,
            """{"error":"internal_failure"}"""));

        var act = async () => await Build(stub).GetAccessTokenAsync(AccountId, CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>();

        var account = await AccountAsync();
        account.Status.Should().Be(GoogleAccountStatus.Healthy);
        account.LastFailureReason.Should().Contain("500");
    }

    // ─────────────────────────────────────────────────────────────────────────── plumbing

    private const string Redirect = "https://drive.example/accounts/callback";

    private GoogleTokenService Build(StubHttpMessageHandler stub, bool configured = true) =>
        new(
            new StubHttpClientFactory(stub),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _protector,
            Credentials(configured),
            _time,
            NullLogger<GoogleTokenService>.Instance);

    /// <summary>
    /// The real resolver over the real store. Nothing about which client refreshes what is worth
    /// asserting against a stub that would agree with whatever the test assumed.
    /// </summary>
    private GoogleOAuthCredentialResolver Credentials(bool configured)
    {
        var settings = configured
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{GoogleOAuthOptions.SectionName}:ClientId"] = ConfiguredClientId,
                [$"{GoogleOAuthOptions.SectionName}:ClientSecret"] = ConfiguredSecret,
                [$"{GoogleOAuthOptions.SectionName}:RedirectUri"] = Redirect,
            }
            : new Dictionary<string, string?>(StringComparer.Ordinal);

        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(GoogleOAuthOptions.SectionName);

        return new GoogleOAuthCredentialResolver(section, Store());
    }

    private GoogleOAuthClientStore Store() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _protector,
        _time,
        NullLogger<GoogleOAuthClientStore>.Instance);

    private Task BindAccountAsync(string clientId) =>
        UpdateAccountAsync(account => account.OAuthClientId = clientId);

    private async Task UpdateAccountAsync(Action<GoogleAccount> change)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        change(await db.GoogleAccounts.SingleAsync(a => a.Id == AccountId));

        await db.SaveChangesAsync();
    }

    private async Task<GoogleAccount> AccountAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        return await db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == AccountId);
    }

    /// <summary>The form Google was actually posted, which is where the client id has to be right.</summary>
    private static Dictionary<string, string> Form(byte[] body) =>
        QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

    private static HttpResponseMessage TokenResponse() => StubResponses.Json(
        HttpStatusCode.OK,
        $$"""
          {
            "access_token": "{{RefreshedToken}}",
            "expires_in": 3599,
            "scope": "https://www.googleapis.com/auth/drive",
            "token_type": "Bearer"
          }
          """);
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
