using System.Net;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The refresh, and the lock around it.
///
/// SQLite stands in for Postgres here — there is no reachable development database on this machine
/// and none of what is being asserted is dialect-specific.
/// </summary>
public sealed class GoogleTokenServiceTests : IDisposable
{
    private const string RefreshedToken = "ya29.refreshed-access-token";

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

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        var account = await db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == AccountId);

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

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();
        var account = await db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == AccountId);

        account.Status.Should().Be(GoogleAccountStatus.Disconnected);
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

    [Fact]
    public async Task Missing_configuration_says_which_keys_are_missing()
    {
        var stub = new StubHttpMessageHandler((_, _) => TokenResponse());
        var service = Build(stub, new GoogleOAuthOptions());

        var act = async () => await service.GetAccessTokenAsync(AccountId, CancellationToken.None);

        await act.Should().ThrowAsync<DriveAccountUnavailableException>()
            .WithMessage("*Google:ClientId*");
    }

    private GoogleTokenService Build(StubHttpMessageHandler stub, GoogleOAuthOptions? options = null) =>
        new(
            new StubHttpClientFactory(stub),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _protector,
            Options.Create(options ?? new GoogleOAuthOptions
            {
                ClientId = "client-id.apps.googleusercontent.com",
                ClientSecret = "client-secret",
                RedirectUri = "https://drive.example/oauth/google",
            }),
            _time,
            NullLogger<GoogleTokenService>.Instance);

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
