using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// The four values every step of Web Push encryption is judged by.
///
/// <para>Returned as a record rather than kept private because a single end-to-end assertion over
/// the finished body tells whoever is reading a failure nothing at all: an encryption that is wrong
/// produces a body that is wrong, and the four places it could have gone wrong are indistinguishable
/// from the outside. RFC 8291 publishes every one of these for its worked example, and
/// <c>WebPushEncryptionTests</c> asserts every one of them — which is the difference between «the
/// key derivation moved» and «something is wrong».</para>
/// </summary>
/// <param name="Ikm">RFC 8291's combined input keying material: the ECDH secret and the device's auth secret.</param>
/// <param name="Prk">RFC 8188's pseudo-random key, extracted from <paramref name="Ikm"/> with the record's salt.</param>
/// <param name="ContentEncryptionKey">The 16-byte AES-GCM key.</param>
/// <param name="Nonce">The 12-byte AES-GCM nonce.</param>
public sealed record WebPushKeys(byte[] Ikm, byte[] Prk, byte[] ContentEncryptionKey, byte[] Nonce);

/// <summary>
/// Web Push message encryption, hand-written: RFC 8291 over RFC 8188's <c>aes128gcm</c> coding.
///
/// <para><b>Why this is not a package.</b> This solution has no third-party dependency at all — the
/// Google Drive client is hand-written, so is AWS SigV4 for the S3 gateway, and the <c>du1</c> file
/// format is this codebase's own AES-GCM. .NET supplies every primitive this needs
/// (<see cref="ECDiffieHellman"/>, <see cref="HKDF"/>, <see cref="AesGcm"/>), the specification is
/// forty lines of pseudocode, and it comes with a complete worked example including its intermediate
/// values. What a package would add here is a supply chain.</para>
///
/// <para><b>The protocol in one paragraph.</b> The device published a P-256 public key and a
/// 16-byte authentication secret. For each message the server generates a <i>new</i> P-256 key pair,
/// does ECDH with the device's key, and folds the shared secret and the auth secret together into
/// one input keying material — that fold is RFC 8291's whole contribution, and it is what stops the
/// push service in the middle, which sees every byte, from being able to decrypt anything. The
/// result is then fed to RFC 8188's ordinary content coding: extract a PRK with a random salt,
/// expand a content-encryption key and a nonce, and AES-128-GCM one record. The server's public key
/// travels in the record's header, in the clear, because the device needs it to do the same ECDH
/// from its side.</para>
///
/// <para><b>Every message gets a new key pair and a new salt.</b> AES-GCM with a repeated key and
/// nonce is not weakened, it is broken — two messages under one pair leak their exclusive-or and the
/// authentication key with it. There is nothing here that could reuse either, and that is by
/// construction rather than by discipline: both are generated inside <see cref="Encrypt"/>, and
/// neither is a parameter any caller can supply. The overload that does take them exists for the
/// specification's own test vector and says so.</para>
/// </summary>
public static class WebPushEncryption
{
    /// <summary>An uncompressed P-256 point: <c>0x04</c>, then X, then Y.</summary>
    public const int PublicKeyLength = 65;

    /// <summary>The device's authentication secret, which RFC 8291 fixes at 16 bytes.</summary>
    public const int AuthSecretLength = 16;

    /// <summary>RFC 8188's salt, which is also fixed at 16 bytes.</summary>
    public const int SaltLength = 16;

    /// <summary>
    /// The record size written into the header.
    ///
    /// <para>4096, which is what every browser accepts and what the specification's example uses. A
    /// push message is one record — see RFC 8291 §4 — so this is a ceiling and never a chunking
    /// decision, and the payloads this product sends are two hundred bytes.</para>
    /// </summary>
    public const int RecordSize = 4096;

    /// <summary>
    /// The most plaintext one record can hold: the record, less the tag, less the delimiter.
    ///
    /// <para>Checked rather than trusted. A payload past this would be encrypted into a record
    /// longer than the header claims, and what the device does with that is «nothing, silently».
    /// </para>
    /// </summary>
    public const int MaxPlaintextLength = RecordSize - TagLength - 1;

    private const int TagLength = 16;

    private const int ContentEncryptionKeyLength = 16;

    private const int NonceLength = 12;

    /// <summary>
    /// The last record's padding delimiter, from RFC 8188 §2.
    ///
    /// <para><c>0x02</c> and not <c>0x01</c>. One says «another record follows» and two says «this
    /// was the last». A single-record message that claims a successor decrypts perfectly and is then
    /// discarded by the receiver as truncated — which is the failure that looks like the network
    /// dropping notifications at random.</para>
    /// </summary>
    private const byte LastRecordDelimiter = 0x02;

    /// <summary>RFC 8291 §3.3. The literal is part of the wire format, not a label.</summary>
    private static ReadOnlySpan<byte> KeyInfoPrefix => "WebPush: info"u8;

    /// <summary>
    /// RFC 8188 §2.2, including its trailing NUL. The whole string is the info parameter.
    ///
    /// <para>An array rather than a <c>u8</c> span because the <see cref="HKDF"/> overload that
    /// returns a key takes arrays; the span one writes into a destination the caller sizes. Built
    /// once, so the conversion is not per message.</para>
    /// </summary>
    private static readonly byte[] ContentEncryptionKeyInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();

    /// <summary>RFC 8188 §2.3, likewise.</summary>
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    /// <summary>
    /// One message for one device: a fresh key pair, a fresh salt, and the body to POST.
    ///
    /// <para>This is the only overload anything in the product calls.</para>
    /// </summary>
    /// <param name="receiverPublicKey">The device's <c>p256dh</c>, 65 bytes.</param>
    /// <param name="authSecret">The device's <c>auth</c>, 16 bytes.</param>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> receiverPublicKey,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> plaintext)
    {
        // Generated here and reachable from nowhere else. See the class remarks: a caller that could
        // supply either of these is a caller that could supply the same one twice.
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        return Encrypt(receiverPublicKey, authSecret, plaintext, sender, salt);
    }

    /// <summary>
    /// The same encryption with the ephemeral key pair and the salt supplied.
    ///
    /// <para><b>For the specification's test vector, and for nothing else.</b> RFC 8291 §5 fixes both
    /// so that its published body is reproducible; without a way to supply them the only thing a
    /// test could assert is that this code agrees with itself, which is what a hand-written protocol
    /// implementation must not be checked by. Reusing a pair across two real messages is an
    /// AES-GCM nonce reuse — see the class remarks — so this stays out of the way of ordinary
    /// callers rather than becoming the convenient overload.</para>
    /// </summary>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> receiverPublicKey,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> plaintext,
        ECDiffieHellman senderKey,
        ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(senderKey);

        if (receiverPublicKey.Length != PublicKeyLength)
        {
            throw new ArgumentException(
                $"A p256dh is {PublicKeyLength} bytes; this one is {receiverPublicKey.Length}.",
                nameof(receiverPublicKey));
        }

        if (authSecret.Length != AuthSecretLength)
        {
            throw new ArgumentException(
                $"An auth secret is {AuthSecretLength} bytes; this one is {authSecret.Length}.",
                nameof(authSecret));
        }

        if (salt.Length != SaltLength)
        {
            throw new ArgumentException(
                $"A salt is {SaltLength} bytes; this one is {salt.Length}.",
                nameof(salt));
        }

        if (plaintext.Length > MaxPlaintextLength)
        {
            throw new ArgumentException(
                $"A push message is one {RecordSize}-byte record, so at most {MaxPlaintextLength} bytes "
                + $"of plaintext; this one is {plaintext.Length}.",
                nameof(plaintext));
        }

        var senderPublicKey = UncompressedPoint(senderKey);
        var sharedSecret = SharedSecret(senderKey, receiverPublicKey);

        var keys = Derive(sharedSecret, authSecret, receiverPublicKey, senderPublicKey, salt);

        // The record: the plaintext, then the delimiter that says this is the last one, then the
        // tag. RFC 8188 pads before the delimiter and this never does — padding hides the length of
        // a message from an observer, and every message this product sends is one of four fixed
        // sentences whose length says nothing that the timing did not already.
        var record = new byte[plaintext.Length + 1 + TagLength];
        plaintext.CopyTo(record);
        record[plaintext.Length] = LastRecordDelimiter;

        using var aes = new AesGcm(keys.ContentEncryptionKey, TagLength);

        aes.Encrypt(
            keys.Nonce,
            record.AsSpan(0, plaintext.Length + 1),
            record.AsSpan(0, plaintext.Length + 1),
            record.AsSpan(plaintext.Length + 1));

        // The header, from RFC 8188 §2.1: salt, record size, the length of the key id, and the key
        // id — which for Web Push is the server's ephemeral public key. It is in the clear on
        // purpose: the device cannot do its half of the ECDH without it.
        var body = new byte[SaltLength + 4 + 1 + PublicKeyLength + record.Length];

        salt.CopyTo(body);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(SaltLength), RecordSize);
        body[SaltLength + 4] = PublicKeyLength;
        senderPublicKey.CopyTo(body.AsSpan(SaltLength + 4 + 1));
        record.CopyTo(body.AsSpan(SaltLength + 4 + 1 + PublicKeyLength));

        return body;
    }

    /// <summary>
    /// The raw ECDH secret — the X coordinate of the shared point, and not a hash of it.
    ///
    /// <para><see cref="ECDiffieHellman.DeriveKeyMaterial"/> would be the obvious call and is the
    /// wrong one: it runs the agreement through a KDF of .NET's choosing. RFC 8291 feeds Z itself
    /// into HKDF, so anything applied here first produces a body that is well formed, encrypts
    /// cleanly, and cannot be decrypted by any browser on earth.</para>
    /// </summary>
    public static byte[] SharedSecret(ECDiffieHellman senderKey, ReadOnlySpan<byte> receiverPublicKey)
    {
        ArgumentNullException.ThrowIfNull(senderKey);

        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,

            // Importing the point is also what checks it. A "public key" that is not on P-256 is
            // refused here rather than producing a shared secret with an attacker's chosen structure
            // in it — this value arrived from a browser over HTTP and is not ours.
            Q = PointOf(receiverPublicKey),
        });

        return senderKey.DeriveRawSecretAgreement(receiver.PublicKey);
    }

    /// <summary>
    /// RFC 8291 §3.4, step by step, with each intermediate kept.
    ///
    /// <para>The two extractions are the part worth reading twice. The first is keyed by the
    /// <i>device's</i> auth secret, which the push service has never seen — that is what makes this
    /// end-to-end rather than merely encrypted in transit. The second is keyed by the record's salt,
    /// which travels in the clear and exists to make every message's key different.</para>
    /// </summary>
    public static WebPushKeys Derive(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> receiverPublicKey,
        ReadOnlySpan<byte> senderPublicKey,
        ReadOnlySpan<byte> salt)
    {
        // HKDF-Extract(salt = auth_secret, IKM = ecdh_secret). The names read backwards against
        // .NET's parameter order and against intuition alike: the auth secret is the salt here, not
        // the keying material. Swapping them derives a key both ends could compute only if both ends
        // made the same mistake.
        // ToArray on both: the only Extract overload that returns a key takes arrays, and the span
        // one writes into a destination the caller sizes. Two 32-byte copies per message is not a
        // cost worth writing the other shape for.
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ikm: sharedSecret.ToArray(), salt: authSecret.ToArray());

        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, KeyInfo(receiverPublicKey, senderPublicKey));

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm: ikm, salt: salt.ToArray());

        return new WebPushKeys(
            ikm,
            prk,
            HKDF.Expand(HashAlgorithmName.SHA256, prk, ContentEncryptionKeyLength, ContentEncryptionKeyInfo),
            HKDF.Expand(HashAlgorithmName.SHA256, prk, NonceLength, NonceInfo));
    }

    /// <summary>
    /// <c>"WebPush: info" || 0x00 || ua_public || as_public</c>.
    ///
    /// <para>The order of the two keys is receiver first and it is not symmetric: the device expands
    /// with its own key first too, and a server that wrote them the other way round would derive a
    /// different key from the same ECDH secret. There is no error message for this anywhere — the
    /// push service accepts the body and the device silently fails to decrypt it.</para>
    /// </summary>
    public static byte[] KeyInfo(ReadOnlySpan<byte> receiverPublicKey, ReadOnlySpan<byte> senderPublicKey)
    {
        var info = new byte[KeyInfoPrefix.Length + 1 + receiverPublicKey.Length + senderPublicKey.Length];

        KeyInfoPrefix.CopyTo(info);
        info[KeyInfoPrefix.Length] = 0;
        receiverPublicKey.CopyTo(info.AsSpan(KeyInfoPrefix.Length + 1));
        senderPublicKey.CopyTo(info.AsSpan(KeyInfoPrefix.Length + 1 + receiverPublicKey.Length));

        return info;
    }

    /// <summary>A key pair's public half as the 65 bytes that go on the wire.</summary>
    public static byte[] UncompressedPoint(ECDiffieHellman key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return UncompressedPoint(key.ExportParameters(false).Q);
    }

    /// <summary>
    /// <c>0x04</c>, then X, then Y, each left-padded to 32 bytes.
    ///
    /// <para>The padding is the part that has to be written down. .NET exports a coordinate as its
    /// minimal big-endian representation, so roughly one key in 256 has an X or a Y that is 31 bytes
    /// long — and a point assembled by concatenation would be 64 bytes for that key, be rejected by
    /// the push service, and work perfectly for the 255 keys anybody tested with.</para>
    /// </summary>
    public static byte[] UncompressedPoint(ECPoint point)
    {
        var encoded = new byte[PublicKeyLength];
        encoded[0] = 0x04;

        CopyRightAligned(point.X, encoded.AsSpan(1, 32));
        CopyRightAligned(point.Y, encoded.AsSpan(33, 32));

        return encoded;
    }

    /// <summary>The X and Y of an uncompressed point, or a refusal that names what arrived.</summary>
    public static ECPoint PointOf(ReadOnlySpan<byte> uncompressed)
    {
        if (uncompressed.Length != PublicKeyLength || uncompressed[0] != 0x04)
        {
            throw new ArgumentException(
                $"A P-256 public key is {PublicKeyLength} bytes beginning 0x04; this is "
                + $"{uncompressed.Length} bytes beginning 0x{(uncompressed.Length > 0 ? uncompressed[0] : 0):x2}.",
                nameof(uncompressed));
        }

        return new ECPoint
        {
            X = uncompressed[1..33].ToArray(),
            Y = uncompressed[33..65].ToArray(),
        };
    }

    /// <summary>UTF-8, because a push payload is JSON and JSON is UTF-8.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static void CopyRightAligned(byte[]? value, Span<byte> destination)
    {
        destination.Clear();

        if (value is null) return;

        if (value.Length > destination.Length)
        {
            throw new ArgumentException(
                $"A P-256 coordinate is at most {destination.Length} bytes; this one is {value.Length}.",
                nameof(value));
        }

        value.CopyTo(destination[(destination.Length - value.Length)..]);
    }
}
