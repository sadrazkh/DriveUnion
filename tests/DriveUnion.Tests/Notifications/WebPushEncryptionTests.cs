using System.Security.Cryptography;
using System.Text;
using DriveUnion.Infrastructure.Push;
using FluentAssertions;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// Web Push message encryption, checked against the specification's own worked example.
///
/// <para><b>Why the numbers below are constants and not computed.</b> A hand-written protocol
/// implementation checked against itself agrees with itself no matter what it does — the property
/// that matters here is not «it decrypts» but «every browser on earth can decrypt it», and the only
/// evidence available for that on a machine with no browser is the specification's published bytes.
/// RFC 8291 §5 fixes a sender key pair, a receiver key pair, an authentication secret, a salt and a
/// plaintext, and prints the body that comes out. Nothing in this file is produced by the code under
/// test. It is the same bargain <c>S3PresignedUrlTests</c> makes with AWS's own example, and for the
/// same reason.</para>
///
/// <para><b>Why every intermediate is asserted and not just the body.</b> They fail in different
/// places. A shared secret that went through a KDF, an IKM whose two keys were concatenated the
/// wrong way round, a PRK extracted with the arguments swapped, a nonce truncated to the wrong
/// length — all four produce a body that is wrong and none of them produces a body that is wrong in
/// a way anybody can read. RFC 8291's Appendix A publishes all of them precisely so that a failure
/// says which step moved, and a single end-to-end assertion would throw that away.</para>
///
/// <para>The base64url decoding here is deliberately not <c>Base64UrlText</c>'s. That type is part
/// of what is under test — a decoder that dropped a byte would make this file agree with itself
/// about the wrong keys — so this reads the vectors through the framework's own primitive.</para>
/// </summary>
public class WebPushEncryptionTests
{
    // ── RFC 8291 §5 and Appendix A, to the byte ──────────────────────────────────────────────────

    private const string Plaintext = "When I grow up, I want to be a watermelon";

    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";

    private const string ReceiverPublicKey =
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";

    private const string ReceiverPrivateKey = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";

    private const string SenderPublicKey =
        "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";

    private const string SenderPrivateKey = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";

    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";

    private const string SharedSecret = "kyrL1jIIOHEzg3sM2ZWRHDRB62YACZhhSlknJ672kSs";

    private const string KeyInfo =
        "V2ViUHVzaDogaW5mbwAEJXGyvs3942BVGq8e0PTNNmwRzr5VX4m8t7GGpTM5FzFo7OLr4BhZe9MEebhuPI-OztV3"
        + "ylkYfpJGmQ22ggCLDgT-M_SrDepxkU21WCP3O1SUj0EwbZIHMtu5pZpTKGSCIA5Zent7wmC6HCJ5mFgJkuk5cwAv"
        + "MBKiiujwa7t45ewP";

    private const string Ikm = "S4lYMb_L0FxCeq0WhDx813KgSYqU26kOyzWUdsXYyrg";

    private const string Prk = "09_eUZGrsvxChDCGRCdkLiDXrReGOEVeSCdCcPBSJSc";

    private const string ContentEncryptionKey = "oIhVW04MRdy2XN9CiKLxTg";

    private const string Nonce = "4h_95klXJ5E_qnoN";

    /// <summary>
    /// The complete body from RFC 8291 §5, with the presentation line breaks taken out.
    ///
    /// <para>144 bytes: a 16-byte salt, a four-byte record size, a one-byte key length, the sender's
    /// 65-byte public key, and a 58-byte record. The specification's own <c>Content-Length: 145</c>
    /// beside it is a typo in the RFC and is not what the base64 decodes to.</para>
    /// </summary>
    private const string Body =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmY"
        + "WAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexS"
        + "gSxsj_Qulcy4a-fN";

    // ── the agreement ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The raw ECDH secret, not a hash of it.</b>
    ///
    /// <para>The first thing this can get wrong and the one that leaves no trace: .NET's obvious
    /// call, <c>DeriveKeyMaterial</c>, runs the agreement through a KDF of its own choosing and
    /// returns 32 bytes that look exactly as plausible as these. Everything downstream would then be
    /// self-consistent, the push service would accept the body, and no browser on earth could open
    /// it.</para>
    /// </summary>
    [Fact]
    public void The_shared_secret_is_the_x_coordinate_the_specification_publishes()
    {
        using var sender = SenderKey();

        Encode(WebPushEncryption.SharedSecret(sender, Decode(ReceiverPublicKey)))
            .Should().Be(SharedSecret, "RFC 8291 Appendix A prints this for these two keys");
    }

    /// <summary>
    /// The same secret from the receiver's side, which is the property the whole protocol rests on.
    ///
    /// <para>Not a duplicate of the test above. That one proves this code agrees with the RFC; this
    /// one proves the agreement is symmetric, which is what makes the device able to derive the same
    /// key from the public half travelling in the record header. A subtly wrong point encoding could
    /// satisfy the first and not the second.</para>
    /// </summary>
    [Fact]
    public void The_device_derives_the_same_secret_from_the_key_in_the_header()
    {
        using var receiver = ReceiverKey();

        Encode(WebPushEncryption.SharedSecret(receiver, Decode(SenderPublicKey)))
            .Should().Be(SharedSecret);
    }

    // ── the key derivation, step by step ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>"WebPush: info" || 0x00 || ua_public || as_public</c>, and the order is not symmetric.
    ///
    /// <para>Asserted on its own because swapping the two keys produces a perfectly valid 144-byte
    /// info string, a perfectly valid IKM, and a message the device silently fails to decrypt. There
    /// is no error anywhere in that path — the push service accepts the body and the notification
    /// never arrives.</para>
    /// </summary>
    [Fact]
    public void The_key_info_is_the_prefix_a_nul_and_the_two_keys_receiver_first()
    {
        Encode(WebPushEncryption.KeyInfo(Decode(ReceiverPublicKey), Decode(SenderPublicKey)))
            .Should().Be(KeyInfo);
    }

    /// <summary>
    /// <b>Every intermediate the specification publishes, in one place.</b>
    ///
    /// <para>The IKM is where the device's authentication secret enters — RFC 8291's entire
    /// contribution over RFC 8188, and what stops the push service in the middle, which sees every
    /// byte, from decrypting anything. The PRK is where the record's salt enters, which is what makes
    /// every message's key different. The last two are the AES-GCM key and nonce, and their lengths
    /// are as load-bearing as their values: 16 and 12.</para>
    /// </summary>
    [Fact]
    public void Every_intermediate_matches_the_specifications_own()
    {
        using var sender = SenderKey();

        var keys = WebPushEncryption.Derive(
            WebPushEncryption.SharedSecret(sender, Decode(ReceiverPublicKey)),
            Decode(AuthSecret),
            Decode(ReceiverPublicKey),
            Decode(SenderPublicKey),
            Decode(Salt));

        Encode(keys.Ikm).Should().Be(Ikm, "the ECDH secret and the auth secret, combined");
        Encode(keys.Prk).Should().Be(Prk, "the IKM extracted with the record's salt");
        Encode(keys.ContentEncryptionKey).Should().Be(ContentEncryptionKey);
        Encode(keys.Nonce).Should().Be(Nonce);

        keys.ContentEncryptionKey.Should().HaveCount(16, "AES-128-GCM takes a 128-bit key");
        keys.Nonce.Should().HaveCount(12, "a longer nonce is hashed by GCM and is not the same nonce");
    }

    // ── the body ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The whole message, byte for byte, as RFC 8291 §5 prints it.</b>
    ///
    /// <para>The key pair and the salt are supplied, which is the only reason this is reproducible —
    /// AES-GCM under a repeated key and nonce is broken rather than weakened, so the overload every
    /// caller in the product uses generates both and takes neither.</para>
    /// </summary>
    [Fact]
    public void The_finished_body_is_the_one_the_specification_publishes()
    {
        using var sender = SenderKey();

        var body = WebPushEncryption.Encrypt(
            Decode(ReceiverPublicKey),
            Decode(AuthSecret),
            Encoding.ASCII.GetBytes(Plaintext),
            sender,
            Decode(Salt));

        Encode(body).Should().Be(Body);
    }

    /// <summary>
    /// The header, read back the way a browser reads it.
    ///
    /// <para>RFC 8188 §2.1: a 16-byte salt, a big-endian record size, one byte of key-id length, and
    /// the key id. Checked separately from the body above so that a header assembled correctly
    /// around a wrong ciphertext, or the reverse, says which half it was.</para>
    /// </summary>
    [Fact]
    public void The_header_carries_the_salt_the_record_size_and_the_senders_public_key()
    {
        var body = Decode(Body);

        Encode(body[..16]).Should().Be(Salt);

        // 0x00001000. A record size the receiver does not expect is a message it discards.
        body[16..20].Should().Equal([0x00, 0x00, 0x10, 0x00]);

        body[20].Should().Be(65, "the key id is an uncompressed P-256 point");
        Encode(body[21..86]).Should().Be(SenderPublicKey);

        // 41 bytes of plaintext, one delimiter, sixteen of tag.
        body.Should().HaveCount(86 + 41 + 1 + 16);
    }

    /// <summary>
    /// The record ends with <c>0x02</c>, and that is not a detail.
    ///
    /// <para>RFC 8188 §2: <c>0x01</c> means «another record follows» and <c>0x02</c> means «this was
    /// the last». A single-record message that claims a successor decrypts perfectly and is then
    /// discarded by the receiver as truncated — which looks exactly like a network dropping
    /// notifications at random.</para>
    /// </summary>
    [Fact]
    public void The_last_record_says_it_is_the_last()
    {
        var body = Decode(Body);
        var record = body[86..];

        using var aes = new AesGcm(Decode(ContentEncryptionKey), 16);

        var opened = new byte[record.Length - 16];
        aes.Decrypt(Decode(Nonce), record[..^16], record[^16..], opened);

        Encoding.ASCII.GetString(opened[..^1]).Should().Be(Plaintext);
        opened[^1].Should().Be(0x02);
    }

    /// <summary>
    /// The device can open a message this code produced with keys it has never seen before.
    ///
    /// <para>The round trip, and it is worth having beside the vector rather than instead of it: the
    /// vector proves the algorithm is the specification's, and this proves the ephemeral key pair
    /// and salt that every real message uses are generated in a form the other side can read. A
    /// point exported 64 bytes long instead of 65 — which happens for about one key in 256, when a
    /// coordinate's leading byte is zero — would pass every assertion above and fail here.</para>
    /// </summary>
    [Fact]
    public void A_message_encrypted_with_a_fresh_key_pair_opens_with_the_devices_own()
    {
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var devicePublic = WebPushEncryption.UncompressedPoint(device);
        var auth = RandomNumberGenerator.GetBytes(16);
        var message = "خبر"u8.ToArray();

        var body = WebPushEncryption.Encrypt(devicePublic, auth, message);

        // Everything the device does, from the header outwards.
        var salt = body[..16];
        var senderPublic = body[21..86];
        var record = body[86..];

        var keys = WebPushEncryption.Derive(
            WebPushEncryption.SharedSecret(device, senderPublic),
            auth,
            devicePublic,
            senderPublic,
            salt);

        using var aes = new AesGcm(keys.ContentEncryptionKey, 16);

        var opened = new byte[record.Length - 16];
        aes.Decrypt(keys.Nonce, record[..^16], record[^16..], opened);

        opened[..^1].Should().Equal(message);
    }

    /// <summary>
    /// Two messages to the same device share no key and no nonce.
    ///
    /// <para>The one property in this file that is not about interoperating. AES-GCM under a
    /// repeated key and nonce pair is not weakened, it is broken: two messages leak their
    /// exclusive-or and the authentication key with it. The ephemeral pair and the salt are both
    /// generated inside <c>Encrypt</c> and are reachable from no caller, which is what makes this a
    /// property of the design rather than of anybody's discipline — this test is what says so.</para>
    /// </summary>
    [Fact]
    public void Two_messages_to_one_device_reuse_neither_the_key_pair_nor_the_salt()
    {
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var devicePublic = WebPushEncryption.UncompressedPoint(device);
        var auth = RandomNumberGenerator.GetBytes(16);

        var first = WebPushEncryption.Encrypt(devicePublic, auth, "one"u8);
        var second = WebPushEncryption.Encrypt(devicePublic, auth, "one"u8);

        Encode(first[..16]).Should().NotBe(Encode(second[..16]), "the salt is drawn per message");
        Encode(first[21..86]).Should().NotBe(Encode(second[21..86]), "the key pair is drawn per message");
        Encode(first).Should().NotBe(Encode(second));
    }

    // ── what is refused ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A key of the wrong length, an auth secret of the wrong length and an oversized payload are all
    /// refused before anything is encrypted.
    ///
    /// <para>These arrive from a browser and from a row a browser wrote. A 64-byte «public key» that
    /// was silently accepted would be a point with a coordinate missing; a payload past one record
    /// would be encrypted into a record longer than the header claims, and what the device does with
    /// that is nothing, silently.</para>
    /// </summary>
    [Fact]
    public void A_key_a_secret_or_a_payload_of_the_wrong_size_is_refused()
    {
        var auth = RandomNumberGenerator.GetBytes(16);
        var key = Decode(ReceiverPublicKey);

        var shortKey = () => WebPushEncryption.Encrypt(key[..64], auth, "x"u8);
        var shortAuth = () => WebPushEncryption.Encrypt(key, auth[..8], "x"u8);
        var huge = () => WebPushEncryption.Encrypt(key, auth, new byte[WebPushEncryption.MaxPlaintextLength + 1]);

        shortKey.Should().Throw<ArgumentException>();
        shortAuth.Should().Throw<ArgumentException>();
        huge.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A point that is not on P-256 is refused rather than agreed with.
    ///
    /// <para>This value came from a browser over HTTP and is not ours. An implementation that
    /// multiplied by an arbitrary point would produce a shared secret with structure an attacker
    /// chose; the key import is what refuses it, and this is what says the import is being relied
    /// on.</para>
    ///
    /// <para>Which exception is the platform's business rather than this product's: OpenSSL checks
    /// the point on import and raises <see cref="CryptographicException"/>, and Windows CNG wraps the
    /// same refusal in a <see cref="PlatformNotSupportedException"/> claiming the curve is not
    /// supported — on a machine that has just used it four times. Both are «no», and
    /// <c>WebPushSender</c> catches both for that reason: one corrupt row must not throw past the
    /// rest of a workspace's devices.</para>
    /// </summary>
    [Fact]
    public void A_public_key_that_is_not_on_the_curve_is_refused()
    {
        var bogus = Decode(ReceiverPublicKey);
        bogus[40] ^= 0xFF;

        using var sender = SenderKey();

        var thrown = Record.Exception(() => WebPushEncryption.SharedSecret(sender, bogus));

        // Asserted on the type rather than through a pattern, because FluentAssertions' Match takes
        // an expression tree and an expression tree may not hold an `is`.
        thrown.Should().BeAssignableTo<Exception>(
            "a point that is not on the curve must be refused rather than agreed with");

        (thrown is CryptographicException or NotSupportedException).Should().BeTrue(
            "the refusal comes from the key import, and which of the two it raises is the platform's "
            + $"business — this one raised {thrown?.GetType().Name}");
    }

    // ── the vectors, read ───────────────────────────────────────────────────────────────────────

    private static ECDiffieHellman SenderKey() => KeyFrom(SenderPrivateKey, SenderPublicKey);

    private static ECDiffieHellman ReceiverKey() => KeyFrom(ReceiverPrivateKey, ReceiverPublicKey);

    private static ECDiffieHellman KeyFrom(string privateKey, string publicKey)
    {
        var point = Decode(publicKey);

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Decode(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    /// <summary>
    /// base64url, through the framework's own primitive.
    ///
    /// <para>Deliberately not <c>Base64UrlText</c>. That type is part of what these tests are
    /// checking, and a decoder that dropped a byte would make this file agree with itself about the
    /// wrong keys — the same reason <c>S3PresignedUrlTests</c> copies its encoding primitives rather
    /// than sharing them with the code under test.</para>
    /// </summary>
    private static byte[] Decode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);

    private static string Encode(ReadOnlySpan<byte> value) =>
        System.Buffers.Text.Base64Url.EncodeToString(value);
}
