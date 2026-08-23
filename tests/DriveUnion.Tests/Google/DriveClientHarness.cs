using DriveUnion.Infrastructure.Google;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// Stands in for the token service so the Drive client can be exercised without a database, a key
/// ring, or a credential. Every request the client makes should carry <see cref="AccessToken"/> —
/// except a chunk write, which has no account id to resolve one from.
/// </summary>
internal sealed class StubTokenService : IGoogleTokenService
{
    public const string AccessToken = "ya29.stub-access-token";

    public int Calls { get; private set; }

    public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(AccessToken);
    }

    public Task<GoogleTokenGrant> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string redirectUri,
        CancellationToken cancellationToken) =>
        Task.FromResult(new GoogleTokenGrant(
            AccessToken,
            "1//stub-refresh-token",
            new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero)));
}

internal static class DriveClientHarness
{
    public static readonly Guid AccountId = Guid.Parse("2f5b6f3a-9a2c-4c6d-8b0e-9f6c1d2a3b4c");

    public static GoogleDriveClient Create(StubHttpMessageHandler stub, TimeProvider? timeProvider = null) =>
        new(
            new HttpClient(stub),
            new StubTokenService(),
            timeProvider ?? new ImmediateTimeProvider(),
            NullLogger<GoogleDriveClient>.Instance);
}
