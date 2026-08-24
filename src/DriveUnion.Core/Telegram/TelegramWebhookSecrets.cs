using System.Buffers.Text;
using System.Security.Cryptography;

namespace DriveUnion.Core.Telegram;

/// <summary>
/// The two random values a webhook registration is made of, and the only place either is produced.
///
/// <para>They are the same shape and the same length as the linking token — 32 bytes, base64url, 43
/// characters — because both are 256-bit secrets that have to survive being put in a URL. Telegram's
/// own rule for <c>secret_token</c> is 1–256 characters of <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>,
/// <c>_</c> and <c>-</c>, which base64url satisfies exactly and with nothing to escape.</para>
///
/// <para><b>Both are rotated on every registration.</b> Re-registering after a leak has to be one
/// button rather than a procedure, and a path segment that outlives its secret is a path somebody
/// already knows.</para>
/// </summary>
public static class TelegramWebhookSecrets
{
    private const int Bytes = 32;

    public static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));
}
