using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DriveUnion.Core.Telegram;

/// <summary>
/// The two secrets in the linking flow, and the only place either is produced or checked.
///
/// Both are one-way at rest. Nothing here can turn a stored hash back into a token or a code, which
/// is the property the whole of <see cref="TelegramLinkToken"/> depends on.
/// </summary>
public static class TelegramLinkSecrets
{
    /// <summary>
    /// 32 bytes, base64url — 43 characters.
    ///
    /// Telegram documents the <c>start</c> parameter as "A-Z, a-z, 0-9, _ and -" and "up to 64
    /// characters", so base64url of 32 bytes fits with room to spare and needs no re-encoding on the
    /// way through a URL.
    /// </summary>
    public const int TokenLength = 43;

    private const int TokenBytes = 32;

    /// <summary>A fresh deep-link token. The caller sees this string once and it is never stored.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// Cheap enough to run before touching the database, and it means a stream of garbage
    /// <c>/start</c> parameters costs one string length rather than one indexed read each.
    /// </summary>
    public static bool IsWellFormedToken(string? token)
    {
        if (token is not { Length: TokenLength }) return false;

        foreach (var c in token)
        {
            var allowed = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!allowed) return false;
        }

        return true;
    }

    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    /// Six digits, uniform over 000000–999999.
    ///
    /// <see cref="RandomNumberGenerator.GetInt32(int, int)"/> rather than a modulo of random bytes:
    /// the modulo is biased, and a biased confirmation code is a smaller code than it looks.
    /// </summary>
    public static string NewConfirmationCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Salted with the token row's id, so two live requests that happen to draw the same six digits
    /// do not store the same hash — and so a stolen table cannot be attacked once for every row at
    /// the same time.
    /// </summary>
    public static string HashConfirmationCode(Guid tokenId, string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var digits = Encoding.UTF8.GetByteCount(code);
        Span<byte> salted = stackalloc byte[16 + digits];

        tokenId.TryWriteBytes(salted[..16]);
        Encoding.UTF8.GetBytes(code, salted[16..]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(salted, hash);

        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Fixed-time, because the comparison is against a value the caller supplies six digits at a
    /// time and an early return would say how many of them were right.
    /// </summary>
    public static bool HashesMatch(string? stored, string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrEmpty(stored)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored),
            Encoding.UTF8.GetBytes(candidate));
    }

    /// <summary>
    /// The bot id hiding in a @BotFather token, which is everything before the colon. Null when the
    /// value does not have that shape, which is also how a mistyped token is caught before it is
    /// stored.
    /// </summary>
    public static long? BotUserIdFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;

        return long.TryParse(
            token.AsSpan(0, colon),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id) && id > 0
            ? id
            : null;
    }
}
