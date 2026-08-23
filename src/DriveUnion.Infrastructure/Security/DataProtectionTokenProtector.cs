using System.Security.Cryptography;
using DriveUnion.Core.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Security;

/// <summary>
/// The Google refresh and access tokens, encrypted at rest with ASP.NET Core Data Protection.
///
/// The key ring is not configured here — <c>Program.cs</c> points it at the database, which is the
/// half of this that actually matters: keys held in a container filesystem are gone on the first
/// redeploy, and every value this class ever wrote becomes undecryptable at the same moment.
/// </summary>
public sealed class DataProtectionTokenProtector : ITokenProtector
{
    /// <summary>
    /// The purpose string is part of the key derivation, so it is effectively part of the ciphertext
    /// format. Changing it does not fail at compile time or at startup — it fails later, once, as
    /// every stored token in the table refusing to decrypt. The <c>.v1</c> is there so that a future
    /// change is at least a deliberate one.
    /// </summary>
    public const string Purpose = "DriveUnion.GoogleTokens.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionTokenProtector> _logger;

    public DataProtectionTokenProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionTokenProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return _protector.Protect(plaintext);
    }

    public string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Null, not a throw. The caller is a token refresh triggered by whoever happened to be
            // uploading a chunk at the time, and "the key ring rotated" is not their error to see.
            // Returning null lets the account be marked for reconnection and the operator told once.
            //
            // FormatException is caught alongside because the ciphertext is base64url text in a
            // database column: a truncated copy-paste or a hand-edited row is a malformed string
            // long before it is a cryptographic failure.
            _logger.LogWarning(
                ex,
                "A protected token could not be decrypted. The Data Protection key that wrote it is "
                + "gone or rotated, and the account it belongs to has to be reconnected.");
            return null;
        }
    }
}
