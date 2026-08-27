using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveUnion.Infrastructure.Push;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DriveUnion.Tests.Notifications;

/// <summary>
/// VAPID — RFC 8292 — which is the half of Web Push that says who is sending.
///
/// <para><b>There is no published signature to compare against, and that is the specification's
/// doing rather than a gap here.</b> ECDSA draws a random <c>k</c> per signature, so two runs over
/// the same bytes with the same key produce different signatures and RFC 8292's example token cannot
/// be reproduced by anybody. What can be pinned is everything either side of it: the JOSE header,
/// the three claims, the <c>aud</c> that has to be an origin and not a URL, the signature's
/// <i>encoding</i> — which is the one thing every implementation gets wrong once — and the fact that
/// the key in <c>k=</c> verifies what is in <c>t=</c>.</para>
///
/// <para>Every failure in this file is the same 401 from the push service, with no detail. That is
/// what makes each of them worth its own assertion.</para>
/// </summary>
public class VapidTests
{
    private const string Endpoint = "https://push.example.net/push/JzLQ3raZJfFBR0aqvOMsLrt54w4rJUsV";

    private const string Subject = "mailto:push@example.com";

    private static readonly DateTimeOffset Expiry = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    // ── the token ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The header, the claims, and a signature the key in the header verifies.</b>
    ///
    /// <para>Three segments, dot-separated, each unpadded base64url. The <c>alg</c> is what the push
    /// service uses to choose a verifier: anything but <c>ES256</c> here is a token nothing will
    /// read.</para>
    /// </summary>
    [Fact]
    public void The_token_is_an_es256_jwt_the_key_beside_it_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var authorization = VapidTokens.Authorization(key, new Uri(Endpoint), Subject, Expiry);

        authorization.Should().StartWith("vapid t=");

        var (token, publicKey) = Split(authorization);
        var parts = token.Split('.');

        parts.Should().HaveCount(3, "a JWT is header.claims.signature");

        Json(parts[0]).RootElement.GetProperty("alg").GetString().Should().Be("ES256");
        Json(parts[0]).RootElement.GetProperty("typ").GetString().Should().Be("JWT");

        var claims = Json(parts[1]).RootElement;

        claims.GetProperty("aud").GetString().Should().Be(
            "https://push.example.net",
            "the audience is the origin of the push resource, never the whole URL");

        claims.GetProperty("sub").GetString().Should().Be(Subject);
        claims.GetProperty("exp").GetInt64().Should().Be(Expiry.ToUnixTimeSeconds());

        // The whole point of the k= parameter: the push service reads the key out of the header and
        // checks the token with it, then compares that key against the one the browser named when it
        // subscribed. A k= that is not this signer's key is a 401.
        using var presented = KeyOf(publicKey);

        presented.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Decode(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    /// <summary>
    /// <b>The signature is 64 raw bytes and not a DER sequence.</b>
    ///
    /// <para>The single most common way a hand-written VAPID token is wrong. .NET's default
    /// <c>SignData</c> emits <c>Rfc3279DerSequence</c> — which is what a certificate carries, is a
    /// perfectly valid ECDSA signature over the same bytes, and is 70-ish bytes beginning
    /// <c>0x30</c>. JOSE fixed ES256 at r and s concatenated, each padded to 32, precisely so a
    /// verifier need not parse ASN.1. Nothing on this side would notice: the 401 arrives at the push
    /// service, with no detail, on every send.</para>
    /// </summary>
    [Fact]
    public void The_signature_is_the_raw_pair_jose_specifies_and_not_der()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var signature = Decode(VapidTokens.Sign(key, "https://push.example.net", Subject, Expiry).Split('.')[2]);

        signature.Should().HaveCount(64, "ES256 is r||s, each padded to 32 bytes");
        signature[0].Should().NotBe(0x30, "0x30 is the tag a DER SEQUENCE starts with");
    }

    /// <summary>
    /// No base64 in the header carries padding.
    ///
    /// <para>RFC 7515 says base64url with no <c>=</c>. A push service verifies the signature over
    /// the two segments exactly as they arrived, so a padded one hashes differently and answers 401
    /// — and a <c>k=</c> with an <c>=</c> in it is not the string the browser stored.</para>
    ///
    /// <para>The four values are checked rather than the whole header, because the header's own
    /// <c>t=</c> and <c>k=</c> are equals signs and always will be.</para>
    /// </summary>
    [Fact]
    public void No_base64_in_the_header_is_padded()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var (token, publicKey) = Split(VapidTokens.Authorization(key, new Uri(Endpoint), Subject, Expiry));

        foreach (var value in token.Split('.').Append(publicKey))
        {
            value.Should().NotContain("=");
            value.Should().NotContainAny("+", "/", "the url-safe alphabet is the point of base64url");
        }
    }

    /// <summary>
    /// The audience is the origin: scheme and host, a port only when it is not the scheme's default.
    ///
    /// <para>Both directions are refusals from the push service and neither says which. A path in the
    /// <c>aud</c> binds the token to one subscription and is rejected; an explicit <c>:443</c> on an
    /// https endpoint is a string that does not match what the service computed, which reads exactly
    /// like a wrong key.</para>
    /// </summary>
    [Theory]
    [InlineData("https://push.example.net/push/abc", "https://push.example.net")]
    [InlineData("https://push.example.net:443/push/abc", "https://push.example.net")]
    [InlineData("https://push.example.net:8443/push/abc", "https://push.example.net:8443")]
    [InlineData("https://fcm.googleapis.com/fcm/send/dGhpcy1pcy1hLXRva2Vu", "https://fcm.googleapis.com")]
    public void The_audience_is_the_endpoints_origin(string endpoint, string expected) =>
        VapidTokens.Audience(new Uri(endpoint)).Should().Be(expected);

    /// <summary>
    /// The token expires, and inside the twenty-four hours RFC 8292 allows.
    ///
    /// <para>A token is a bearer credential for «post to this endpoint». Twelve hours is half the
    /// ceiling and is not minutes on purpose: clocks disagree, and a server running fast would
    /// otherwise mint tokens that are already expired when they land.</para>
    /// </summary>
    [Fact]
    public void The_lifetime_is_inside_the_specifications_ceiling() =>
        VapidTokens.Lifetime.Should().BeGreaterThan(TimeSpan.Zero)
            .And.BeLessThan(TimeSpan.FromHours(24));

    // ── the keys, and the states an operator can leave them in ──────────────────────────────────

    /// <summary>Nothing set. The ordinary state of a development machine, and it must not be an error.</summary>
    [Fact]
    public void An_unconfigured_deployment_says_so_and_offers_no_key()
    {
        var status = Credentials([]).Describe();

        status.State.Should().Be(VapidState.NotConfigured);

        // Null is the gate the notifications screen reads: no key, no subscribe control. A browser
        // will mint a subscription against any 65 bytes and every send to it would be a 403 for the
        // life of the row.
        status.PublicKey.Should().BeNull();
        status.IsReady.Should().BeFalse();
    }

    /// <summary>
    /// A generated pair is ready, and the key handed to the browser is the one that was configured.
    /// </summary>
    [Fact]
    public void A_generated_pair_is_ready_and_publishes_its_public_half()
    {
        var (publicKey, privateKey) = VapidCredentials.Generate();

        var credentials = Credentials(new Dictionary<string, string?>
        {
            ["PublicKey"] = publicKey,
            ["PrivateKey"] = privateKey,
            ["Subject"] = Subject,
        });

        var status = credentials.Describe();

        status.State.Should().Be(VapidState.Ready);
        status.PublicKey.Should().Be(publicKey);
        status.Problem.Should().BeNull();
        credentials.Subject.Should().Be(Subject);

        using var signer = credentials.CreateSigningKey();
        signer.Should().NotBeNull();
    }

    /// <summary>
    /// <b>A public key that is not the private key's own half is refused on the screen.</b>
    ///
    /// <para>The one configuration mistake with no other symptom. Every push service answers a
    /// mismatched pair with a 403 and no detail, on every send, for ever — so without this the
    /// operator's evidence is «notifications do not arrive», and nothing in the product would ever
    /// name the cause. It is caught by signing and verifying, which is the only check that can see
    /// it without doing elliptic-curve arithmetic by hand.</para>
    /// </summary>
    [Fact]
    public void A_public_key_from_another_pair_is_refused_rather_than_used()
    {
        var (_, privateKey) = VapidCredentials.Generate();
        var (otherPublic, _) = VapidCredentials.Generate();

        var status = Credentials(new Dictionary<string, string?>
        {
            ["PublicKey"] = otherPublic,
            ["PrivateKey"] = privateKey,
        }).Describe();

        status.State.Should().Be(VapidState.Unusable);
        status.PublicKey.Should().BeNull("a key that cannot sign must not be handed to a browser");
        status.Problem.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Half-configured is named as the half that is missing.
    ///
    /// <para>By far the commonest way this goes wrong: a public key pasted into the environment and
    /// a private key that never left the machine it was generated on. «Push:PrivateKey is not set»
    /// is an instruction; «the key pair is not usable» is a puzzle.</para>
    /// </summary>
    [Theory]
    [InlineData("PublicKey")]
    [InlineData("PrivateKey")]
    public void A_half_configured_pair_names_the_half_that_is_missing(string present)
    {
        var (publicKey, privateKey) = VapidCredentials.Generate();

        var status = Credentials(new Dictionary<string, string?>
        {
            [present] = present == "PublicKey" ? publicKey : privateKey,
        }).Describe();

        status.State.Should().Be(VapidState.Unusable);
        status.Problem.Should().Contain(present == "PublicKey" ? "PrivateKey" : "PublicKey");
    }

    /// <summary>
    /// Blank counts as absent, which is what lets <c>appsettings.Development.json</c> ship the keys
    /// with empty values as documentation — the same bargain the Google section makes.
    /// </summary>
    [Fact]
    public void An_empty_value_is_not_a_configured_one() =>
        Credentials(new Dictionary<string, string?>
        {
            ["PublicKey"] = "",
            ["PrivateKey"] = "   ",
            ["Subject"] = "",
        }).Describe().State.Should().Be(VapidState.NotConfigured);

    /// <summary>
    /// Text that is not a key at all is refused, and named as what it is.
    ///
    /// <para>These are operator-typed values. A truncated paste and a key from another curve both
    /// arrive here, and both would otherwise become an exception inside a background worker that
    /// nobody is watching.</para>
    /// </summary>
    [Theory]
    [InlineData("not base64 at all!!", "base64")]
    [InlineData("c2hvcnQ", "bytes")]
    public void A_public_key_that_is_not_one_is_refused(string publicKey, string mentions)
    {
        var (_, privateKey) = VapidCredentials.Generate();

        var status = Credentials(new Dictionary<string, string?>
        {
            ["PublicKey"] = publicKey,
            ["PrivateKey"] = privateKey,
        }).Describe();

        status.State.Should().Be(VapidState.Unusable);
        status.Problem.Should().Contain(mentions);
    }

    /// <summary>
    /// A generated public key is the 65 bytes a browser's <c>applicationServerKey</c> wants.
    ///
    /// <para>65 and never 64: .NET exports a coordinate minimally, so roughly one key in 256 has an X
    /// or a Y that is 31 bytes long. A point assembled by concatenation would be short for that key,
    /// be refused by the browser, and work perfectly for the other 255 — which is a defect nobody
    /// finds by testing.</para>
    /// </summary>
    [Fact]
    public void A_generated_public_key_is_an_uncompressed_point()
    {
        for (var i = 0; i < 32; i++)
        {
            var (publicKey, privateKey) = VapidCredentials.Generate();

            var bytes = Decode(publicKey);

            bytes.Should().HaveCount(65);
            bytes[0].Should().Be(0x04);

            Decode(privateKey).Should().HaveCount(32, "a P-256 private scalar is 32 bytes, padded");
        }
    }

    /// <summary>
    /// A subject that is neither a <c>mailto:</c> nor an <c>https:</c> URI falls back rather than
    /// being sent. Some push services refuse a token whose <c>sub</c> they cannot parse.
    /// </summary>
    [Theory]
    [InlineData("operator@example.com")]
    [InlineData("http://example.com")]
    [InlineData("")]
    public void A_subject_that_is_not_a_contact_uri_is_not_used(string configured) =>
        Credentials(new Dictionary<string, string?> { ["Subject"] = configured })
            .Subject.Should().Be(VapidCredentials.UnsetSubject);

    // ── reading the vectors ─────────────────────────────────────────────────────────────────────

    private static VapidCredentials Credentials(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static (string Token, string PublicKey) Split(string authorization)
    {
        var parameters = authorization["vapid ".Length..].Split(", ");

        return (parameters[0]["t=".Length..], parameters[1]["k=".Length..]);
    }

    private static ECDsa KeyOf(string publicKey)
    {
        var point = Decode(publicKey);

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    private static JsonDocument Json(string segment) => JsonDocument.Parse(Decode(segment));

    private static byte[] Decode(string value) => Base64Url.DecodeFromChars(value);
}
