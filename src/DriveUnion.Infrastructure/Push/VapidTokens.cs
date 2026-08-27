using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// VAPID — RFC 8292 — which is how a push service learns who is sending, without anybody holding an
/// account.
///
/// <para><b>What it is for.</b> A push endpoint is a URL with no credential on it: whoever has the
/// string can post to the device. VAPID adds a signature over «I am the server this subscription was
/// made for, and here is where to complain about me», signed by the same key pair the browser was
/// given when it subscribed. That is what stops a leaked endpoint from being usable by anybody else
/// — the push service checks the signature against the key the browser named — and it is what gives
/// Apple's and Google's services somebody to rate-limit and somebody to email.</para>
///
/// <para><b>The whole of it is a JWT and one header.</b> <c>Authorization: vapid t=&lt;jwt&gt;,
/// k=&lt;public key&gt;</c>, where the JWT is ES256 over three claims: the origin of the endpoint,
/// an expiry, and a contact address. There is no library here for the same reason there is none for
/// SigV4 or for Drive: .NET signs ES256 in one call, and the encoding is base64url and a dot.</para>
/// </summary>
public static class VapidTokens
{
    /// <summary>
    /// The header's scheme name, lower-case, as RFC 8292 §3 registers it.
    ///
    /// <para>Some push services compare it case-sensitively and some do not, so it is written once
    /// here in the form the specification uses rather than spelled at a call site.</para>
    /// </summary>
    public const string Scheme = "vapid";

    /// <summary>
    /// How long a token is good for.
    ///
    /// <para>Twelve hours, against the specification's ceiling of twenty-four. A token is a bearer
    /// credential for «post to this endpoint», so the window is how long a captured
    /// <c>Authorization</c> header stays useful; half the maximum costs nothing because a token is
    /// minted per request. It is not minutes because clocks disagree — a server running fast against
    /// a push service running slow would mint tokens that are already expired, and the answer would
    /// be a 401 that names nothing.</para>
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// The complete <c>Authorization</c> value for one request to one endpoint.
    /// </summary>
    /// <param name="key">The application server's key pair. Not disposed here; the caller owns it.</param>
    /// <param name="endpoint">The subscription's own URL. Only its origin is signed.</param>
    /// <param name="subject">A <c>mailto:</c> or <c>https:</c> contact for whoever runs this server.</param>
    public static string Authorization(ECDsa key, Uri endpoint, string subject, DateTimeOffset expiry)
    {
        ArgumentNullException.ThrowIfNull(key);

        var token = Sign(key, Audience(endpoint), subject, expiry);
        var publicKey = Base64UrlText.Encode(WebPushEncryption.UncompressedPoint(key.ExportParameters(false).Q));

        // One space after the scheme and one after the comma, which is what every implementation
        // emits and what the ABNF allows. The parameters are unquoted: RFC 8292 defines them as
        // token68-shaped values, and a quoted string here is refused by some services with a 401
        // that says «invalid JWT» and means «I could not find one».
        return $"{Scheme} t={token}, k={publicKey}";
    }

    /// <summary>
    /// The signed token on its own, for a caller that wants to inspect it — and for the tests.
    /// </summary>
    public static string Sign(ECDsa key, string audience, string subject, DateTimeOffset expiry)
    {
        ArgumentNullException.ThrowIfNull(key);

        var header = Base64UrlText.Encode(JsonSerializer.SerializeToUtf8Bytes(
            new VapidHeader("JWT", "ES256"),
            VapidJson.Options));

        var claims = Base64UrlText.Encode(JsonSerializer.SerializeToUtf8Bytes(
            new VapidClaims(audience, expiry.ToUnixTimeSeconds(), subject),
            VapidJson.Options));

        var signingInput = $"{header}.{claims}";

        // IeeeP1363FixedFieldConcatenation — r and s, each padded to 32 bytes, concatenated. .NET's
        // default is Rfc3279DerSequence, which is what a certificate carries and what every push
        // service refuses: JOSE fixed ES256 at the raw 64 bytes precisely so that a verifier does not
        // have to parse ASN.1. The two are both valid ECDSA signatures over the same bytes, so
        // nothing here would notice — the 401 arrives at the push service.
        var signature = key.SignData(
            System.Text.Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64UrlText.Encode(signature)}";
    }

    /// <summary>
    /// The <c>aud</c> claim: the origin of the push resource, and never the path.
    ///
    /// <para>«Origin» is scheme, host, and the port only when it is not the scheme's default —
    /// RFC 6454's serialisation, which is what the specification names. Including the path would
    /// bind the token to one subscription and would be refused; including <c>:443</c> on an https
    /// endpoint produces a string that does not match what the push service computed, which is a 401
    /// that reads exactly like a wrong key.</para>
    /// </summary>
    public static string Audience(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.IsDefaultPort
            ? $"{endpoint.Scheme}://{endpoint.Host}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}");
    }

    private sealed record VapidHeader(
        [property: JsonPropertyName("typ")] string Type,
        [property: JsonPropertyName("alg")] string Algorithm);

    private sealed record VapidClaims(
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("exp")] long Expiry,
        [property: JsonPropertyName("sub")] string Subject);

    private static class VapidJson
    {
        /// <summary>
        /// Nothing indented and nothing escaped beyond what JSON requires.
        ///
        /// <para>The default encoder escapes <c>+</c> and every non-ASCII character to <c>\uXXXX</c>,
        /// which is legal JSON and a longer string — and the string's own bytes are what is signed.
        /// It would still verify, because both halves are the same bytes; it is written down because
        /// the temptation with a JWT is always to «tidy» the JSON, and any change to it changes the
        /// signature.</para>
        /// </summary>
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };
    }
}
