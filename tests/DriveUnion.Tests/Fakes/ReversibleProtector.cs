using System.Text;
using DriveUnion.Core.Abstractions;

namespace DriveUnion.Tests.Fakes;

/// <summary>
/// Reversible, and deliberately not encryption — what the tests need is a wrapper that does not
/// contain its own input, so «the secret is not sitting in the column» is a real assertion rather
/// than a tautology about ciphertext.
///
/// <para><see cref="Broken"/> makes every stored value undecryptable, which is what a lost Data
/// Protection key looks like from here.</para>
///
/// <para>It began nested inside <c>TelegramTestHarness</c>, where it was written for the bot token.
/// It is here because a second slice needed it — the S3 gateway's access keys are encrypted for the
/// reason a bot token is, and for a reason of their own: SigV4 has to recompute the client's HMAC,
/// so a one-way hash cannot work. A fake two slices share is not one slice's.</para>
/// </summary>
public sealed class ReversibleProtector : ITokenProtector
{
    private const string Prefix = "wrapped:";

    /// <summary>Set to true to simulate a key that no longer exists.</summary>
    public bool Broken { get; set; }

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string protectedValue)
    {
        if (Broken || protectedValue is null || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
    }
}
