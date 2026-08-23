using System.Security.Cryptography;

namespace DriveUnion.Core.Sharing;

public interface ISlugGenerator
{
    string Next();
}

/// <summary>
/// Eight lowercase alphanumerics, from a CSPRNG — <c>kx91mzq4</c>.
///
/// The comp draws six (<c>/d/kx91mz</c>), which is 36^6 ≈ 2.2 billion. That sounds like a lot until
/// you put it behind an anonymous public route: a few hundred hosts guessing steadily find a live
/// link in minutes, not years. Eight is 36^8 ≈ 2.8 trillion — a thousandfold harder — and costs two
/// characters of URL nobody reads.
///
/// A slug is still only an identifier. Everything that actually protects a file — expiry, download
/// cap, and the password in M4 — is enforced server-side. Collisions are caught by a unique index
/// and retried at the call site rather than assumed away.
/// </summary>
public sealed class SlugGenerator : ISlugGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    public const int SlugLength = 8;

    public string Next()
    {
        Span<char> slug = stackalloc char[SlugLength];
        for (var i = 0; i < SlugLength; i++)
        {
            slug[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(slug);
    }

    public static bool IsWellFormed(string? slug)
    {
        if (slug is null || slug.Length != SlugLength) return false;

        foreach (var c in slug)
        {
            if (!Alphabet.Contains(c)) return false;
        }

        return true;
    }
}
