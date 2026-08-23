namespace DriveUnion.Infrastructure.Google;

/// <summary>Who the account is and how full it is, from Drive's <c>about</c> resource.</summary>
public sealed record GoogleAboutInfo(string Email, long LimitBytes, long UsageBytes);

/// <summary>
/// Reads <c>about</c> with a token handed in directly, rather than one resolved from an account id.
///
/// This exists for exactly one moment: connecting an account. The tokens are in hand, the row does
/// not exist yet, and the email that will identify it can only come from Google. Everything after
/// that point goes through <see cref="Core.Abstractions.IDriveClient"/> and an account id.
///
/// The address comes from the Drive scope's own <c>about</c> resource on purpose — asking for
/// <c>openid</c> or <c>email</c> as well would widen the consent screen for no gain.
/// </summary>
public interface IGoogleAboutReader
{
    Task<GoogleAboutInfo> GetAboutAsync(string accessToken, CancellationToken cancellationToken);
}
