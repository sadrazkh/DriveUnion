using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DriveUnion.Web.S3;

/// <summary>What a presented Authorization header claimed, before any of it is believed.</summary>
public sealed record SignatureV4Header(
    string AccessKeyId,
    string DateStamp,
    string Region,
    string Service,
    IReadOnlyList<string> SignedHeaders,
    string Signature);

/// <summary>
/// What a presigned URL's query string claimed, before any of it is believed.
/// </summary>
/// <param name="AmzDate">
/// The <c>X-Amz-Date</c> parameter as it arrived, not a reformatting of <paramref name="SignedAt"/>.
/// It goes into the string to sign verbatim, so keeping the original text beside the parsed instant
/// is what stops a round trip through <see cref="DateTimeOffset"/> from silently signing a different
/// string than the client did.
/// </param>
public sealed record SignatureV4Presigned(
    SignatureV4Header Header,
    string AmzDate,
    DateTimeOffset SignedAt,
    TimeSpan Lifetime)
{
    /// <summary>The instant after which this URL is a refusal rather than an object.</summary>
    public DateTimeOffset ExpiresAt => SignedAt + Lifetime;
}

/// <summary>
/// AWS Signature Version 4, verified.
///
/// <para>The protocol in one paragraph: the client builds a <i>canonical request</i> — method, path,
/// sorted query, the headers it chose to sign, and a hash of the body — hashes it, wraps that in a
/// <i>string to sign</i> carrying the timestamp and the credential scope, and HMACs it with a key
/// derived from the secret through four chained HMACs. The server does all of it again and compares.
/// Every step is specified to the byte, and getting one wrong produces a signature mismatch with no
/// clue which step it was — so each step below says what it is doing and why.</para>
///
/// <para><b>Scope of this implementation</b>, stated rather than discovered: both halves of SigV4 —
/// the signature in an <c>Authorization</c> header, and the signature in a query string, which is
/// what <c>aws s3 presign</c> and every SDK's <c>GetPreSignedURL</c> produce. POST policies are not
/// accepted, because a browser form post is not reachable through the operations this gateway
/// offers. Region and service in the credential scope are checked for shape and then used verbatim —
/// a gateway that insisted on <c>us-east-1</c> would refuse a client configured for anywhere else
/// for no reason, since there is one storage pool and it is not in a region.</para>
///
/// <para><b>The two halves differ in four places and nowhere else.</b> The signature lives in
/// <c>X-Amz-Signature</c> rather than in a header; that one parameter is left out of the canonical
/// query while every other <c>X-Amz-*</c> stays in; the payload is the literal
/// <see cref="UnsignedPayload"/> because there was no body when the URL was made; and the request
/// carries its own lifetime in <c>X-Amz-Expires</c> instead of borrowing the clock-skew window.
/// Everything after that — canonical request, string to sign, the four chained HMACs — is the same
/// code, which is the point of having written it once.</para>
/// </summary>
public static class SignatureV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The body hash a client sends when it does not want to hash the body at all.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>
    /// The body hash a client sends when the body is <c>aws-chunked</c>.
    ///
    /// <para>What arrives then is not the object: it is the object cut into chunks, each preceded by
    /// its length and its own signature. <c>AwsChunkedStream</c> is what turns it back into the
    /// object; this constant is what tells the gateway to expect that.</para>
    /// </summary>
    public const string StreamingPayload = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";

    /// <summary>
    /// How far a request's timestamp may be from ours.
    ///
    /// <para>Fifteen minutes is what AWS allows, and it is a replay window rather than a courtesy:
    /// a signature is valid for as long as its timestamp is accepted, so this is the length of time
    /// a captured request stays useful. Wider would be kinder to a machine with a bad clock and is
    /// not worth what it costs.</para>
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest a presigned URL may live, which is AWS's own cap rather than a number of ours.
    ///
    /// <para>Seven days is where every SDK refuses to sign, so a URL asking for more was not made by
    /// a client this gateway can hope to interoperate with. It is also the outer edge of what a
    /// bearer credential in a query string should ever be: a presigned URL survives in browser
    /// history, in a <c>Referer</c>, in whatever chat client unfurled it, and the cap is the only
    /// thing bounding how long a copy of it stays useful.</para>
    /// </summary>
    public static readonly TimeSpan MaxPresignedLifetime = TimeSpan.FromDays(7);

    // The six query parameters a presigned URL carries. Named constants because two places have to
    // agree on them exactly — the parser below and the canonical query, which must omit
    // «X-Amz-Signature» and keep the other five.
    public const string AlgorithmParameter = "X-Amz-Algorithm";
    public const string CredentialParameter = "X-Amz-Credential";
    public const string DateParameter = "X-Amz-Date";
    public const string ExpiresParameter = "X-Amz-Expires";
    public const string SignedHeadersParameter = "X-Amz-SignedHeaders";
    public const string SignatureParameter = "X-Amz-Signature";

    /// <summary>
    /// Parses the header, or null when it is not an <c>AWS4-HMAC-SHA256</c> one this can read.
    /// </summary>
    public static SignatureV4Header? ParseHeader(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        if (!authorization.StartsWith(Algorithm, StringComparison.Ordinal)) return null;

        var parts = authorization[Algorithm.Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string? credential = null;
        string? signedHeaders = null;
        string? signature = null;

        foreach (var part in parts)
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) continue;

            var name = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();

            switch (name)
            {
                case "Credential": credential = value; break;
                case "SignedHeaders": signedHeaders = value; break;
                case "Signature": signature = value; break;
                default: break;
            }
        }

        if (credential is null || signedHeaders is null || signature is null) return null;

        return FromCredential(credential, signedHeaders, signature);
    }

    /// <summary>
    /// A presigned URL's query parameters, parsed, or null when they are not a set that can be
    /// verified at all.
    ///
    /// <para>One null for every shape of «this cannot be checked» — a parameter missing, an algorithm
    /// this does not implement, a credential that is not a scope, a date that is not a date, and an
    /// <c>X-Amz-Expires</c> that is not a positive number of seconds inside the week AWS caps it at.
    /// S3 answers all of them with <c>AuthorizationQueryParametersError</c>, so there is nothing for
    /// a caller to do with them separately, and naming which parameter offended tells somebody
    /// probing the gateway how far they got.</para>
    ///
    /// <para>Nothing here is compared against the clock. Whether a URL is <i>expired</i> depends on
    /// the server's own time and belongs where the rest of the timing checks are; this decides only
    /// whether the query is a signature at all.</para>
    /// </summary>
    public static SignatureV4Presigned? ParsePresigned(
        string? algorithm,
        string? credential,
        string? amzDate,
        string? expires,
        string? signedHeaders,
        string? signature)
    {
        // Ordinal and exact. «aws4-hmac-sha256» is not the algorithm name the client put in its own
        // canonical query, so accepting it here would only produce a mismatch two steps later.
        if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal)) return null;

        if (string.IsNullOrEmpty(credential)) return null;
        if (string.IsNullOrEmpty(signedHeaders)) return null;
        if (string.IsNullOrEmpty(signature)) return null;

        if (ParseAmzDate(amzDate) is not { } signedAt) return null;

        // Whole seconds and nothing else: NumberStyles.None refuses a sign, a decimal point,
        // exponent notation and surrounding whitespace, so «-1», «3600.0» and « 60» are all refused
        // rather than guessed at. An expiry the signer did not agree to is not one to invent.
        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        // Range-checked as a number before it becomes a TimeSpan. TimeSpan.FromSeconds throws on
        // anything near long.MaxValue, so a comparison written the other way round would turn an
        // absurd X-Amz-Expires into a 500 instead of a refusal.
        if (seconds <= 0 || seconds > (long)MaxPresignedLifetime.TotalSeconds) return null;

        return FromCredential(credential, signedHeaders, signature) is { } header
            ? new SignatureV4Presigned(header, amzDate!, signedAt, TimeSpan.FromSeconds(seconds))
            : null;
    }

    /// <summary>
    /// The signature this request should carry, given the secret.
    /// </summary>
    /// <param name="canonicalUri">
    /// The path, already URI-encoded the way S3 wants it: each segment encoded, <c>/</c> left alone.
    /// S3 is the one AWS service that does <b>not</b> double-encode here, which is the single most
    /// common reason a hand-written verifier disagrees with every client in the world.
    /// </param>
    /// <param name="canonicalQuery">Sorted <c>k=v</c> pairs, both halves encoded, joined with <c>&amp;</c>.</param>
    /// <param name="headers">Every header the client listed in <c>SignedHeaders</c>, by lower-case name.</param>
    /// <param name="payloadHash">
    /// Whatever the client put in <c>x-amz-content-sha256</c> — a hex hash, <see cref="UnsignedPayload"/>
    /// or <see cref="StreamingPayload"/>. It is signed as the literal string the client sent, so the
    /// gateway never has to have read the body to check the signature.
    /// </param>
    public static string Compute(
        SignatureV4Header header,
        string secret,
        string method,
        string canonicalUri,
        string canonicalQuery,
        IReadOnlyDictionary<string, string> headers,
        string payloadHash,
        string amzDate)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(headers);

        var canonicalHeaders = new StringBuilder();

        foreach (var name in header.SignedHeaders)
        {
            // Values are trimmed and inner runs of whitespace collapsed. Clients do this before
            // signing, so a verifier that skipped it would disagree with any request whose header
            // happened to carry a double space.
            var value = headers.TryGetValue(name, out var raw) ? Collapse(raw) : string.Empty;

            canonicalHeaders.Append(name).Append(':').Append(value).Append('\n');
        }

        var canonicalRequest = string.Join(
            '\n',
            method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeaders.ToString(),
            string.Join(';', header.SignedHeaders),
            payloadHash);

        var scope = $"{header.DateStamp}/{header.Region}/{header.Service}/aws4_request";

        var stringToSign = string.Join(
            '\n',
            Algorithm,
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        return Hex(HmacSha256(SigningKey(secret, header), Encoding.UTF8.GetBytes(stringToSign)));
    }

    /// <summary>
    /// Whether a presented signature is the expected one, compared in fixed time.
    ///
    /// <para>Ordinal and case-sensitive: both sides are lower-case hex by construction, and an
    /// equality that folded case would accept a signature that is not the one that was computed.</para>
    /// </summary>
    public static bool Matches(string expected, string presented)
    {
        if (expected is null || presented is null) return false;
        if (expected.Length != presented.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presented));
    }

    /// <summary>
    /// The four chained HMACs. Each one keys the next, which is what stops a leaked signing key
    /// being useful for another day, another region or another service.
    /// </summary>
    public static byte[] SigningKey(string secret, SignatureV4Header header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var date = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secret}"), Encoding.UTF8.GetBytes(header.DateStamp));
        var region = HmacSha256(date, Encoding.UTF8.GetBytes(header.Region));
        var service = HmacSha256(region, Encoding.UTF8.GetBytes(header.Service));

        return HmacSha256(service, "aws4_request"u8.ToArray());
    }

    /// <summary>
    /// <c>x-amz-date</c> parsed, or null. Also accepts a plain <c>Date</c>, which some clients send.
    /// </summary>
    public static DateTimeOffset? ParseAmzDate(string? amzDate) =>
        DateTimeOffset.TryParseExact(
            amzDate,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Percent-encoding as AWS defines it, which is <b>not</b> <c>Uri.EscapeDataString</c>'s.
    ///
    /// <para>Unreserved is A–Z a–z 0–9 <c>-</c> <c>_</c> <c>.</c> <c>~</c> and everything else is
    /// <c>%XX</c> in upper-case hex. The differences that bite are the space, which must be
    /// <c>%20</c> and never <c>+</c>, and the tilde, which older escapers encode and AWS does not.
    /// </para>
    /// </summary>
    public static string UriEncode(string value, bool encodeSlash)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>
    /// The credential scope, split and checked, or null when it is not one.
    ///
    /// <para><c>&lt;access key&gt;/&lt;yyyyMMdd&gt;/&lt;region&gt;/&lt;service&gt;/aws4_request</c> —
    /// five parts, and the last is a fixed terminator. Anything else is not a scope this can verify
    /// against. Shared by the header and the query string because AWS specifies one credential
    /// format for both, and two copies of this would eventually disagree about which.</para>
    /// </summary>
    private static SignatureV4Header? FromCredential(string credential, string signedHeaders, string signature)
    {
        var scope = credential.Split('/');

        if (scope.Length != 5 || scope[4] != "aws4_request") return null;
        if (scope[0].Length == 0 || scope[1].Length != 8) return null;

        return new SignatureV4Header(
            scope[0],
            scope[1],
            scope[2],
            scope[3],
            [.. signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries)],
            signature);
    }

    private static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    /// <summary>Trimmed, with inner whitespace runs collapsed to one space.</summary>
    private static string Collapse(string value)
    {
        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var wasSpace = false;

        foreach (var c in trimmed)
        {
            var isSpace = c is ' ' or '\t';

            if (isSpace && wasSpace) continue;

            builder.Append(isSpace ? ' ' : c);
            wasSpace = isSpace;
        }

        return builder.ToString();
    }
}
