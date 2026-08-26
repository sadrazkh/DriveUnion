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
/// AWS Signature Version 4, verified.
///
/// <para>The protocol in one paragraph: the client builds a <i>canonical request</i> — method, path,
/// sorted query, the headers it chose to sign, and a hash of the body — hashes it, wraps that in a
/// <i>string to sign</i> carrying the timestamp and the credential scope, and HMACs it with a key
/// derived from the secret through four chained HMACs. The server does all of it again and compares.
/// Every step is specified to the byte, and getting one wrong produces a signature mismatch with no
/// clue which step it was — so each step below says what it is doing and why.</para>
///
/// <para><b>Scope of this implementation</b>, stated rather than discovered: header-based SigV4
/// only. Presigned URLs and POST policies are not accepted, because neither is reachable through
/// the operations this gateway offers. Region and service in the credential scope are checked for
/// shape and then used verbatim — a gateway that insisted on <c>us-east-1</c> would refuse a client
/// configured for anywhere else for no reason, since there is one storage pool and it is not in a
/// region.</para>
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

        // <access key>/<yyyyMMdd>/<region>/<service>/aws4_request — five parts, and the last is a
        // fixed terminator. Anything else is not a scope this can verify against.
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
