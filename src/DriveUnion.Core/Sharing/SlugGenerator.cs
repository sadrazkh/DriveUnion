using System.Security.Cryptography;

namespace DriveUnion.Core.Sharing;

public interface ISlugGenerator
{
    string Next();
}

/// <summary>
/// Six lowercase alphanumerics, from a CSPRNG — <c>kx91mz</c>, matching the shape the design shows.
///
/// 36^6 is about 2.2 billion, so a guess is not a threat, but a slug is still only an identifier:
/// everything that actually protects a file (expiry, download cap, password in M4) is enforced
/// server-side. Collisions are handled by a unique index and a retry at the call site, not by
/// hoping — at a million links the birthday odds are not negligible.
/// </summary>
public sealed class SlugGenerator : ISlugGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    public const int SlugLength = 6;

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
