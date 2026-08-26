using DriveUnion.Core.Api;
using DriveUnion.Core.Application;

namespace DriveUnion.Web.S3;

/// <summary>Why a request was refused, as the <c>Code</c> an S3 client expects.</summary>
public enum S3Refusal
{
    None,
    AccessDenied,
    InvalidAccessKeyId,
    SignatureDoesNotMatch,
    RequestTimeTooSkewed,
    MissingSecurityHeader,
}

public sealed record S3Authentication(S3Signer? Signer, S3Refusal Refusal, string PayloadHash)
{
    public bool Succeeded => Signer is not null && Refusal == S3Refusal.None;
}

/// <summary>
/// Turns an incoming request into a signer, or into the reason it will not be served.
///
/// <para>Everything the protocol needs is read off the request here, in one place, so the gateway's
/// actions can be about objects. The rebuilt canonical request has to be byte-identical to the one
/// the client built — see <see cref="SignatureV4"/> for the steps — and the two most common ways to
/// get it wrong are both handled here: the path must be re-encoded from the <i>raw</i> target rather
/// than the decoded route value, and the query must be sorted and encoded rather than passed
/// through.</para>
/// </summary>
public sealed class S3RequestAuthenticator(IS3Credentials credentials, TimeProvider clock)
{
    public async Task<S3Authentication> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadHash = request.Headers["x-amz-content-sha256"].ToString();

        if (string.IsNullOrEmpty(payloadHash)) payloadHash = SignatureV4.UnsignedPayload;

        var header = SignatureV4.ParseHeader(request.Headers.Authorization.ToString());

        if (header is null) return new S3Authentication(null, S3Refusal.MissingSecurityHeader, payloadHash);

        var amzDate = request.Headers["x-amz-date"].ToString();

        if (SignatureV4.ParseAmzDate(amzDate) is not { } signedAt)
        {
            return new S3Authentication(null, S3Refusal.MissingSecurityHeader, payloadHash);
        }

        // Checked before the secret is looked up, so a replay of an old request costs one comparison
        // rather than a query.
        if (Abs(clock.GetUtcNow() - signedAt) > SignatureV4.MaxClockSkew)
        {
            return new S3Authentication(null, S3Refusal.RequestTimeTooSkewed, payloadHash);
        }

        var signer = await credentials.ResolveAsync(header.AccessKeyId, cancellationToken);

        if (signer is null) return new S3Authentication(null, S3Refusal.InvalidAccessKeyId, payloadHash);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in header.SignedHeaders)
        {
            // «host» is not in Headers on every server, because Kestrel models it separately.
            headers[name] = name == "host"
                ? request.Host.Value ?? string.Empty
                : request.Headers[name].ToString();
        }

        var expected = SignatureV4.Compute(
            header,
            signer.Secret,
            request.Method,
            CanonicalUri(request),
            CanonicalQuery(request),
            headers,
            payloadHash,
            amzDate);

        return SignatureV4.Matches(expected, header.Signature)
            ? new S3Authentication(signer, S3Refusal.None, payloadHash)
            : new S3Authentication(null, S3Refusal.SignatureDoesNotMatch, payloadHash);
    }

    /// <summary>
    /// The path, encoded the way S3 signs it.
    ///
    /// <para>Built from <see cref="HttpRequest.Path"/>, which ASP.NET has already decoded, and then
    /// re-encoded segment by segment with the slash left alone. S3 is the one AWS service that does
    /// not double-encode this, and a verifier that used the raw target instead would disagree with
    /// any client whose key contains a space or a non-Latin character — which, in a product whose
    /// customers name files in Persian, is most of them.</para>
    /// </summary>
    private static string CanonicalUri(HttpRequest request)
    {
        var path = request.Path.Value ?? "/";

        return path.Length == 0
            ? "/"
            : string.Join('/', path.Split('/').Select(segment => SignatureV4.UriEncode(segment, encodeSlash: true)));
    }

    /// <summary>
    /// The query, sorted by encoded name then encoded value, each pair <c>name=value</c>.
    ///
    /// <para>A parameter with no value still gets its <c>=</c> — <c>?acl</c> signs as <c>acl=</c> —
    /// which is the detail that breaks every sub-resource request if it is missed.</para>
    /// </summary>
    private static string CanonicalQuery(HttpRequest request)
    {
        var pairs = new List<(string Name, string Value)>();

        foreach (var (name, values) in request.Query)
        {
            foreach (var value in values)
            {
                pairs.Add((SignatureV4.UriEncode(name, true), SignatureV4.UriEncode(value ?? string.Empty, true)));
            }
        }

        return string.Join(
            '&',
            pairs
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value}"));
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}

/// <summary>Whether a scope may do a thing, and the words a refusal uses.</summary>
public static class S3Permissions
{
    public static bool MayWrite(ApiScope scope) => scope == ApiScope.Write;
}
