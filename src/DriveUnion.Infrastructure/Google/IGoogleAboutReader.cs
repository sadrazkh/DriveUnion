namespace DriveUnion.Infrastructure.Google;

/// <summary>Who the account is and how full it is, from Drive's <c>about</c> resource.</summary>
/// <param name="Email">What the operator reads on the card. Not an identity — see the next one.</param>
/// <param name="PermissionId">
/// Drive's own stable id for this account, and the only thing here that identifies it.
///
/// The address cannot: Gmail treats <c>archive.main@gmail.com</c>, <c>archive.main+cold@gmail.com</c>
/// and <c>archivemain@gmail.com</c> as one mailbox, and Google reports back whichever spelling was
/// typed. Keyed on the address, the same account connected twice under two spellings becomes two
/// rows, two labels and five terabytes of pool capacity that does not exist — and the router in M2
/// would then send uploads to an account it thinks is empty.
///
/// Null only for a Drive response that carried no <c>user.permissionId</c>, which is not documented
/// to happen and is tolerated rather than trusted.
/// </param>
public sealed record GoogleAboutInfo(
    string Email,
    string? PermissionId,
    long LimitBytes,
    long UsageBytes);

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
