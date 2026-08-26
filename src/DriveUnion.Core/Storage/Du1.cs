using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DriveUnion.Core.Storage;

/// <summary>
/// The <c>du1</c> format, in C#.
///
/// <para><b>This is a second implementation of something that already exists</b>, and that is worth
/// stating plainly: <c>Scripts/crypto/format.ts</c> is the first, it is the one every browser runs,
/// and it is the definition. Nothing here may drift from it — a segment this writes must open in
/// that, and a file that browser wrote must open here. Two golden fixtures, one produced by each
/// side, are committed for exactly that reason, because nothing else can prove it: the two halves
/// have no shared runtime and a unit test on either side would only be agreeing with itself.</para>
///
/// <para><b>Why a second implementation exists at all.</b> A file the server fetches from a URL
/// never passes through a browser, so the only place it can be encrypted is here. That is a weaker
/// property than the browser's and it is labelled differently everywhere it is shown — see
/// <see cref="FileEncryption.SealedBy"/>. What it is <i>not</i> is a second format: the customer
/// opens both kinds on the same page, with the same island, by typing the same sort of secret.</para>
/// </summary>
public static class Du1
{
    /// <summary>The one scheme there is. See format.ts.</summary>
    public const int Scheme = 1;

    /// <summary>Plaintext bytes per segment. 1 MiB, as format.ts says and for the reasons it gives.</summary>
    public const int SegmentSize = 1024 * 1024;

    /// <summary>AES-GCM's tag, appended to every segment's ciphertext.</summary>
    public const int TagBytes = 16;

    /// <summary>The random half of the nonce. The other four bytes are the segment index.</summary>
    public const int NoncePrefixBytes = 8;

    /// <summary>The whole nonce: the prefix and the index.</summary>
    public const int NonceBytes = 12;

    /// <summary>OWASP's 2023 figure for PBKDF2-SHA256, matching format.ts.</summary>
    public const int KdfIterations = 600_000;

    /// <summary>The salt, and the nonce that wraps the content key.</summary>
    public const int SaltBytes = 16;

    /// <summary>AES-256.</summary>
    public const int KeyBytes = 32;

    /// <summary>Ciphertext bytes for a plaintext of this length.</summary>
    public static long CipherLength(long plaintextLength) =>
        plaintextLength == 0 ? 0 : plaintextLength + (TagBytes * SegmentCount(plaintextLength));

    public static int SegmentCount(long plaintextLength) =>
        (int)((plaintextLength + SegmentSize - 1) / SegmentSize);

    /// <summary>
    /// Twelve bytes: the file's prefix, then the index, big-endian.
    ///
    /// <para>The content key is random and per file, so a nonce repeats only if a segment index
    /// does — which it cannot, within one file.</para>
    /// </summary>
    public static byte[] NonceFor(ReadOnlySpan<byte> prefix, int index)
    {
        var nonce = new byte[NonceBytes];

        prefix[..NoncePrefixBytes].CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixBytes), (uint)index);

        return nonce;
    }

    /// <summary>
    /// What a segment authenticates besides itself: <c>du1</c>, its index, and whether it is last.
    ///
    /// <para>Without the index two segments could be swapped and both tags would still verify.
    /// Without the final flag a truncated file decrypts cleanly and simply ends early.</para>
    /// </summary>
    public static byte[] AadFor(int index, bool isFinal)
    {
        var aad = new byte[8];

        Encoding.ASCII.GetBytes("du1", aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(3), (uint)index);
        aad[7] = isFinal ? (byte)1 : (byte)0;

        return aad;
    }

    /// <summary>
    /// The wrapping key, derived from what the customer typed.
    ///
    /// <para>NFKC first, because the browser normalises before encoding and a passphrase with a
    /// composed character in it would otherwise derive two different keys depending on which side
    /// wrapped it.</para>
    /// </summary>
    public static byte[] DeriveWrappingKey(string secret, ReadOnlySpan<byte> salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormKC)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
    }

    /// <summary>
    /// A content key sealed under a wrapping key: twelve bytes of nonce, then the sealed key.
    ///
    /// <para>The nonce travels in front rather than in its own field, exactly as envelope.ts writes
    /// it — this has to be readable by the same <c>unseal</c> on the other end, and a second layout
    /// would be a second thing to get wrong.</para>
    /// </summary>
    public static byte[] WrapKey(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var envelope = new byte[NonceBytes + contentKey.Length + TagBytes];

        nonce.CopyTo(envelope.AsSpan());

        using var aes = new AesGcm(wrappingKey, TagBytes);

        aes.Encrypt(
            nonce,
            contentKey,
            envelope.AsSpan(NonceBytes, contentKey.Length),
            envelope.AsSpan(NonceBytes + contentKey.Length, TagBytes));

        return envelope;
    }

    /// <summary>The content key back, or null when the secret is wrong.</summary>
    public static byte[]? UnwrapKey(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> wrappingKey)
    {
        if (envelope.Length <= NonceBytes + TagBytes) return null;

        var sealedLength = envelope.Length - NonceBytes - TagBytes;
        var key = new byte[sealedLength];

        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);

            aes.Decrypt(
                envelope[..NonceBytes],
                envelope.Slice(NonceBytes, sealedLength),
                envelope.Slice(NonceBytes + sealedLength, TagBytes),
                key);

            return key;
        }
        catch (CryptographicException)
        {
            // A wrong wrapping key produces a failed tag and nothing else, so there is no oracle
            // here beyond «yes or no» — the same answer envelope.ts gives, for the same reason.
            return null;
        }
    }

    /// <summary>
    /// One segment, sealed: ciphertext then tag, which is the order Web Crypto produces and
    /// therefore the order the browser expects.
    /// </summary>
    public static byte[] EncryptSegment(
        ReadOnlySpan<byte> contentKey,
        ReadOnlySpan<byte> noncePrefix,
        int index,
        bool isFinal,
        ReadOnlySpan<byte> plain)
    {
        var sealedBytes = new byte[plain.Length + TagBytes];

        using var aes = new AesGcm(contentKey, TagBytes);

        aes.Encrypt(
            NonceFor(noncePrefix, index),
            plain,
            sealedBytes.AsSpan(0, plain.Length),
            sealedBytes.AsSpan(plain.Length, TagBytes),
            AadFor(index, isFinal));

        return sealedBytes;
    }

    /// <summary>One segment back, or null when it does not verify.</summary>
    public static byte[]? DecryptSegment(
        ReadOnlySpan<byte> contentKey,
        ReadOnlySpan<byte> noncePrefix,
        int index,
        bool isFinal,
        ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < TagBytes) return null;

        var plain = new byte[sealedBytes.Length - TagBytes];

        try
        {
            using var aes = new AesGcm(contentKey, TagBytes);

            aes.Decrypt(
                NonceFor(noncePrefix, index),
                sealedBytes[..plain.Length],
                sealedBytes.Slice(plain.Length, TagBytes),
                plain,
                AadFor(index, isFinal));

            return plain;
        }
        catch (CryptographicException)
        {
            // Every reason at once — the wrong key, a flipped bit, a segment moved to another index,
            // a truncated file whose last segment is not marked final. One answer, because they have
            // one remedy: this is not the file you asked for.
            return null;
        }
    }
}
