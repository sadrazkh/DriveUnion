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

    /// <summary>
    /// A presigned URL used after the moment its own <c>X-Amz-Expires</c> named. Distinct from
    /// <see cref="RequestTimeTooSkewed"/> because the two are opposite complaints — one is about the
    /// caller's clock and the other about the signer's own deadline — even though S3 puts
    /// <c>AccessDenied</c> on the wire for this one.
    /// </summary>
    RequestExpired,

    /// <summary>
    /// A query string that claims to be presigned and cannot be read as one. <c>400</c> and
    /// <c>AuthorizationQueryParametersError</c>, which is S3's answer for a query whose parameters
    /// are wrong rather than a signature that is.
    /// </summary>
    MalformedPresignedQuery,

    /// <summary>A presigned URL carrying a verb that a presigned URL is not allowed to carry here.</summary>
    PresignedMethodNotAllowed,
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
///
/// <para><b>Two ways in, and they are told apart by what is presented</b> rather than by route or
/// verb: an <c>Authorization</c> header, or a signature in the query string. The header wins when
/// both arrive, which is safe rather than lax — a header signature covers the whole query, including
/// every <c>X-Amz-*</c> parameter in it, so a request carrying both is one that had to be signed
/// correctly by the header's key anyway.</para>
///
/// <para><b>A presigned URL may only read.</b> See <see cref="MayBePresigned"/> for the argument;
/// the refusal is here, once, ahead of the credential lookup, so that no route can acquire a
/// presigned write by forgetting to ask.</para>
/// </summary>
public sealed class S3RequestAuthenticator(IS3Credentials credentials, TimeProvider clock)
{
    public async Task<S3Authentication> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadHash = request.Headers["x-amz-content-sha256"].ToString();

        if (string.IsNullOrEmpty(payloadHash)) payloadHash = SignatureV4.UnsignedPayload;

        var header = SignatureV4.ParseHeader(request.Headers.Authorization.ToString());

        // Either parameter is enough to know a caller meant to presign. Keying only on
        // «X-Amz-Algorithm» would send a URL that carries a signature and nothing else down the
        // header path, where the answer is «you did not sign this» — which is the opposite of true
        // and unhelpful to whoever is debugging their SDK.
        if (header is null
            && (request.Query.ContainsKey(SignatureV4.AlgorithmParameter)
                || request.Query.ContainsKey(SignatureV4.SignatureParameter)))
        {
            return await AuthenticatePresignedAsync(request, cancellationToken);
        }

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

        var expected = SignatureV4.Compute(
            header,
            signer.Secret,
            request.Method,
            CanonicalUri(request),
            CanonicalQuery(request),
            SignedHeaderValues(request, header),
            payloadHash,
            amzDate);

        return SignatureV4.Matches(expected, header.Signature)
            ? new S3Authentication(signer, S3Refusal.None, payloadHash)
            : new S3Authentication(null, S3Refusal.SignatureDoesNotMatch, payloadHash);
    }

    /// <summary>
    /// The same verification, with the signature read out of the query string instead of a header.
    /// </summary>
    private async Task<S3Authentication> AuthenticatePresignedAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // UNSIGNED-PAYLOAD, unconditionally, and never whatever an «x-amz-content-sha256» header
        // says. A presigned URL was signed before there was a request to send: the signer had no
        // body to hash, and every SDK puts this literal in the canonical request it signs. Reading
        // the header instead would let a caller change a byte of the signed input by sending a
        // header the signature never covered.
        const string payloadHash = SignatureV4.UnsignedPayload;

        var presigned = SignatureV4.ParsePresigned(
            algorithm: request.Query[SignatureV4.AlgorithmParameter].ToString(),
            credential: request.Query[SignatureV4.CredentialParameter].ToString(),
            amzDate: request.Query[SignatureV4.DateParameter].ToString(),
            expires: request.Query[SignatureV4.ExpiresParameter].ToString(),
            signedHeaders: request.Query[SignatureV4.SignedHeadersParameter].ToString(),
            signature: request.Query[SignatureV4.SignatureParameter].ToString());

        if (presigned is null) return new S3Authentication(null, S3Refusal.MalformedPresignedQuery, payloadHash);

        // Before the clock, before the secret, before any HMAC: the verb is the one thing about a
        // presigned request that no amount of correct signing can make acceptable.
        if (!MayBePresigned(request.Method))
        {
            return new S3Authentication(null, S3Refusal.PresignedMethodNotAllowed, payloadHash);
        }

        var now = clock.GetUtcNow();

        // Dated ahead of us by more than header auth would tolerate. Without this check the seven-day
        // cap means nothing at all: an X-Amz-Date a year out with X-Amz-Expires=604800 is a URL that
        // works for a year and a week, and the cap it passed on the way in was decoration.
        if (presigned.SignedAt - now > SignatureV4.MaxClockSkew)
        {
            return new S3Authentication(null, S3Refusal.RequestTimeTooSkewed, payloadHash);
        }

        // Expiry is exact, with none of the skew above added to it. Unlike a header signature — where
        // the fifteen minutes is our tolerance for somebody else's clock — this deadline is the
        // customer's own promise about how long a link they handed out stays alive, and a gateway
        // that quietly granted every link another quarter of an hour would make «expires in sixty
        // seconds» false. A URL signed in the past and still inside its lifetime is the ordinary
        // case and is not skew: that is what presigning is for.
        if (now > presigned.ExpiresAt) return new S3Authentication(null, S3Refusal.RequestExpired, payloadHash);

        var signer = await credentials.ResolveAsync(presigned.Header.AccessKeyId, cancellationToken);

        if (signer is null) return new S3Authentication(null, S3Refusal.InvalidAccessKeyId, payloadHash);

        var expected = SignatureV4.Compute(
            presigned.Header,
            signer.Secret,
            request.Method,
            CanonicalUri(request),

            // Every X-Amz-* parameter is signed except the signature itself, which could not be: it
            // is the output of hashing the canonical request, so a canonical request containing it
            // is one the client had no way to build.
            CanonicalQuery(request, omitting: SignatureV4.SignatureParameter),
            SignedHeaderValues(request, presigned.Header),
            payloadHash,
            presigned.AmzDate);

        return SignatureV4.Matches(expected, presigned.Header.Signature)
            ? new S3Authentication(signer, S3Refusal.None, payloadHash)
            : new S3Authentication(null, S3Refusal.SignatureDoesNotMatch, payloadHash);
    }

    /// <summary>
    /// What a presigned URL may ask for: reads, and nothing else.
    ///
    /// <para><b>The argument.</b> A presigned URL <i>is</i> a bearer credential, written in the one
    /// part of a request that leaks by design — it lands in browser history, in a <c>Referer</c>, in
    /// a proxy's access log, in whatever chat client unfurled the link, and in the screenshot
    /// somebody pastes into a ticket. Read and write are not symmetric under that assumption. A
    /// leaked read URL exposes the one object its maker already meant to share. A leaked write URL
    /// lets a stranger <b>replace</b> that object — this gateway's PUT stores the new bytes and
    /// sends the old file to the trash — and spend the workspace's Drive quota doing it, for as long
    /// as seven days, against a customer who has no way to know it happened.</para>
    ///
    /// <para>There is a second reason, and it is technical rather than a judgement call: the PUT path
    /// decides how to read the body from <c>x-amz-decoded-content-length</c> and from whether the
    /// payload hash says <c>aws-chunked</c>. A presigned URL signs neither — the payload is
    /// <c>UNSIGNED-PAYLOAD</c> and those are headers, not query parameters — so a presigned PUT would
    /// have to take the length and the framing of an unauthenticated body on trust.</para>
    ///
    /// <para>The cost is real and is accepted: «upload without credentials» is a genuine S3 workflow
    /// and it is not available here. A caller that needs to write signs an <c>Authorization</c>
    /// header, which is a live secret in the hands of the party doing the writing rather than a
    /// standing invitation in a URL.</para>
    ///
    /// <para>HEAD is allowed with GET. It reads metadata and returns no body, and an SDK that
    /// presigns a HEAD is asking «is it still there» — refusing that would be arbitrary.</para>
    /// </summary>
    private static bool MayBePresigned(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    /// <summary>
    /// The value of every header the client said it signed, by lower-case name.
    /// </summary>
    private static Dictionary<string, string> SignedHeaderValues(HttpRequest request, SignatureV4Header header)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in header.SignedHeaders)
        {
            // «host» is not in Headers on every server, because Kestrel models it separately.
            headers[name] = name == "host"
                ? request.Host.Value ?? string.Empty
                : request.Headers[name].ToString();
        }

        return headers;
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
    /// <param name="omitting">
    /// A parameter to leave out, which is <c>X-Amz-Signature</c> and only ever that. Matched by name
    /// and case-insensitively, before encoding: what is being excluded is a parameter by identity,
    /// not a particular sequence of bytes.
    /// </param>
    private static string CanonicalQuery(HttpRequest request, string? omitting = null)
    {
        var pairs = new List<(string Name, string Value)>();

        foreach (var (name, values) in request.Query)
        {
            if (omitting is not null && string.Equals(name, omitting, StringComparison.OrdinalIgnoreCase)) continue;

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
