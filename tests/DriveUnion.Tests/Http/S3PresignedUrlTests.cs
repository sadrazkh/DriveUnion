using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Api;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Web.S3;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Http;

/// <summary>
/// Presigned URLs: the half of SigV4 where the signature travels in the query string and the caller
/// holds no credential at all.
///
/// <para><b>The signing is written out here from the specification</b>, as it is in
/// <see cref="S3GatewayTests"/> and for a sharper reason. A signer checked against itself agrees with
/// itself no matter what it does, and this feature has exactly one interesting failure mode — a
/// canonical form that no AWS client produces, which would pass a round-trip test and refuse every
/// real <c>aws s3 presign</c> URL forever. So the first test below is AWS's own published example,
/// constants and all: a fixed key, a fixed second in 2013, and the signature the documentation says
/// comes out. Nothing in it is computed by the code under test.</para>
///
/// <para>The two encoding primitives are copied rather than shared with the header suite on purpose.
/// The value of these tests is that they are an independent second implementation; a helper both
/// suites imported would be a third thing that could be wrong in one place.</para>
/// </summary>
public class S3PresignedUrlTests
{
    private const string Region = "us-east-1";
    private const string Service = "s3";

    /// <summary>
    /// AWS's worked example for query-string authentication, reproduced to the byte.
    ///
    /// <para>Published in «Authenticating Requests: Using Query Parameters (AWS Signature Version
    /// 4)». Three of its numbers are asserted rather than one, because they fail in different
    /// places: the canonical request's hash pins the <i>shape</i> of the string being signed — the
    /// sorted query with <c>X-Amz-Signature</c> absent, the trailing blank line after the headers,
    /// <c>UNSIGNED-PAYLOAD</c> as the last line — while the signature pins the four chained HMACs on
    /// top of it. A test that only checked the signature would say «mismatch» and leave whoever is
    /// reading it to guess which half moved.</para>
    /// </summary>
    [Fact]
    public void The_specifications_own_presigned_example_signs_to_the_signature_it_publishes()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        const string amzDate = "20130524T000000Z";
        const string host = "examplebucket.s3.amazonaws.com";

        const string canonicalQuery =
            "X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20130524T000000Z"
            + "&X-Amz-Expires=86400"
            + "&X-Amz-SignedHeaders=host";

        var canonicalRequest = string.Join(
            '\n', "GET", "/test.txt", canonicalQuery, $"host:{host}\n", "host", "UNSIGNED-PAYLOAD");

        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))
            .Should().Be(
                "3bfa292879f6447bbcda7001decf97f4a54dc650c8942174ae0a9121cf58ad04",
                "this is the hash the specification prints for this canonical request");

        var header = new SignatureV4Header(
            "AKIAIOSFODNN7EXAMPLE", "20130524", Region, Service, ["host"], "not read by Compute");

        var signature = SignatureV4.Compute(
            header,
            secret,
            "GET",
            "/test.txt",
            canonicalQuery,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["host"] = host },
            SignatureV4.UnsignedPayload,
            amzDate);

        signature.Should().Be(
            "aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            "an S3 gateway that disagrees with this string disagrees with every SDK in the world");
    }

    /// <summary>The same example read the other way: the query string parsed back into a claim.</summary>
    [Fact]
    public void The_specifications_own_query_string_parses_into_the_credential_it_names()
    {
        var parsed = SignatureV4.ParsePresigned(
            algorithm: "AWS4-HMAC-SHA256",
            credential: "AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request",
            amzDate: "20130524T000000Z",
            expires: "86400",
            signedHeaders: "host",
            signature: "aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404");

        parsed.Should().NotBeNull();
        parsed!.Header.AccessKeyId.Should().Be("AKIAIOSFODNN7EXAMPLE");
        parsed.Header.DateStamp.Should().Be("20130524");
        parsed.Header.Region.Should().Be(Region);
        parsed.Header.Service.Should().Be(Service);
        parsed.Header.SignedHeaders.Should().Equal(["host"]);
        parsed.Lifetime.Should().Be(TimeSpan.FromHours(24));

        // The date is kept as the text that arrived as well as as an instant: that text goes into
        // the string to sign verbatim, and a reformatting round trip would sign something else.
        parsed.AmzDate.Should().Be("20130524T000000Z");
        parsed.SignedAt.Should().Be(new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));
        parsed.ExpiresAt.Should().Be(new DateTimeOffset(2013, 5, 25, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_presigned_url_serves_the_object_with_no_authorization_header_at_all()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        var path = $"/s3/{bucket}/{seeded.FileName}";

        using var client = harness.NewClient();
        using var response = await client.GetAsync(Presign(client, HttpMethod.Get, path, id, secret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        // The point of the whole feature: nothing was presented but the URL. If any part of the
        // gateway had reached for an Authorization header this could not have reached the bytes.
        response.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");

        // HEAD too. An SDK that presigns one is asking «is it still there», and it reads nothing a
        // presigned GET would not have handed over anyway.
        using var probe = new HttpRequestMessage(
            HttpMethod.Head, Presign(client, HttpMethod.Head, path, id, secret));

        using var head = await client.SendAsync(probe);

        head.StatusCode.Should().Be(HttpStatusCode.OK);
        head.Headers.ETag.Should().NotBeNull();
    }

    /// <summary>
    /// A signature in the query string does not override one in a header.
    ///
    /// <para>Presented with both, the gateway verifies the header — which is safe rather than lax,
    /// because a header signature covers the whole query string including every <c>X-Amz-*</c>
    /// parameter in it. The order is pinned here because either order is defensible and a silent
    /// change of mind would move which of two signatures a request is actually being judged by.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_query_does_not_override_an_authorization_header_that_is_also_there()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Presign(client, HttpMethod.Get, $"/s3/{bucket}/{seeded.FileName}", id, secret));

        var now = DateTimeOffset.UtcNow;

        // Everything a header signature needs except a real signature, so that the refusal names the
        // step it got to. A header missing «x-amz-date» would be refused as MissingSecurityHeader,
        // which is also not a 200 but would not say which mechanism did the refusing.
        request.Headers.TryAddWithoutValidation(
            "x-amz-date", now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={id}/{now.UtcDateTime:yyyyMMdd}/{Region}/{Service}/aws4_request, "
            + $"SignedHeaders=host, Signature={new string('0', 64)}");

        using var response = await client.SendAsync(request);

        // SignatureDoesNotMatch: the header was parsed, timed, resolved to a key and then compared —
        // the whole header path, run to its end. A gateway that had preferred the query beside it
        // would have answered 200 and quietly ignored a credential it was handed.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SignatureDoesNotMatch");
    }

    /// <summary>
    /// A key that has to be percent-encoded, which is where a hand-written signer and a real client
    /// part company.
    ///
    /// <para>The space and the Persian characters are the case this product actually has: customers
    /// name files in Persian, so «most keys need encoding» is the normal condition and not an edge.
    /// S3 signs the path with each segment encoded and the slash left alone, and it is the one AWS
    /// service that does not double-encode it.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_url_signs_a_key_that_has_to_be_encoded()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4", fileName: "گزارش سالانه ۱۴۰۴.pdf", mimeType: "application/pdf");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        using var client = harness.NewClient();
        var url = Presign(client, HttpMethod.Get, $"/s3/{bucket}/{seeded.FileName}", id, secret);

        url.Should().Contain("%20", "a space in a key signs and travels as %20, never as a plus");

        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);
    }

    /// <summary>
    /// The property that separates a presigned URL from a signed header: age is not the thing that
    /// disqualifies it.
    ///
    /// <para>Header auth refuses anything more than fifteen minutes from the server's clock —
    /// <c>SignatureV4.MaxClockSkew</c>, and <c>S3GatewayTests</c> asserts it at two hours old. A
    /// presigned URL two hours old is not stale, it is two hours into the week its maker gave it.
    /// Both halves are asserted in one test because the interesting claim is the difference between
    /// them: the same signing time, one lifetime that covers it and one that does not.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_url_lives_by_its_own_expiry_and_not_by_the_clock_skew_window()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);
        var path = $"/s3/{bucket}/{seeded.FileName}";
        var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2);

        using var client = harness.NewClient();

        using var alive = await client.GetAsync(
            Presign(client, HttpMethod.Get, path, id, secret, at: twoHoursAgo, expires: "604800"));

        alive.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a URL made two hours ago for a week is two hours into its week, not eight times too skewed");

        using var expired = await client.GetAsync(
            Presign(client, HttpMethod.Get, path, id, secret, at: twoHoursAgo, expires: "3600"));

        expired.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // AccessDenied and not a code of our own: S3 answers an expired presigned URL this way and
        // clients switch on the code rather than read the message.
        var body = await expired.Content.ReadAsStringAsync();

        body.Should().Contain("AccessDenied");
        body.Should().Contain("Request has expired");
        body.Should().NotContain("RequestTimeTooSkewed", "the signature is not stale, its lifetime is over");
    }

    /// <summary>
    /// A signing time in the future is bounded by the same fifteen minutes header auth allows.
    ///
    /// <para>The measurement that motivates it: without this check, <c>X-Amz-Date=20990101T000000Z</c>
    /// with <c>X-Amz-Expires=604800</c> is a URL that answers 200 for the next seventy-three years,
    /// because <c>now &lt;= signedAt + lifetime</c> is true the whole way. The seven-day cap it
    /// passed on the way in would have been decoration.</para>
    /// </summary>
    [Fact]
    public async Task A_url_signed_far_ahead_of_the_server_is_refused_rather_than_given_decades()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);
        var path = $"/s3/{bucket}/{seeded.FileName}";

        using var client = harness.NewClient();

        using var distant = await client.GetAsync(Presign(
            client, HttpMethod.Get, path, id, secret,
            at: new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), expires: "604800"));

        distant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await distant.Content.ReadAsStringAsync()).Should().Contain("RequestTimeTooSkewed");

        // A window and not a ban. Clients sign a little ahead all the time — a machine whose clock
        // runs a few minutes fast is the ordinary case, and refusing it would refuse real traffic.
        using var slightly = await client.GetAsync(Presign(
            client, HttpMethod.Get, path, id, secret, at: DateTimeOffset.UtcNow.AddMinutes(5)));

        slightly.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A presigned URL may not write, and the refusal is about the mechanism rather than the key.
    ///
    /// <para>Signed with a <b>Write</b> credential on purpose. A read-scoped key would be refused by
    /// <c>S3Permissions.MayWrite</c> for an entirely different reason, and the test would pass while
    /// proving nothing about presigning. The argument for the rule is on
    /// <c>S3RequestAuthenticator.MayBePresigned</c>: a URL is the part of a request that leaks by
    /// design, and this gateway's PUT replaces an object and trashes the one it replaced.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_url_may_not_write_however_correctly_it_is_signed()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Write);
        var bucket = SlugOf(harness, seeded.TenantId);
        var path = $"/s3/{bucket}/{seeded.FileName}";

        using var client = harness.NewClient();

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(
                method, Presign(client, method, path, id, secret))
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("overwritten")),
            };

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden, $"a presigned {method.Method} is not a thing this gateway has");

            (await response.Content.ReadAsStringAsync()).Should().Contain("AccessDenied");
        }

        // The object is exactly as it was: not replaced, not trashed, not joined by a second copy.
        await using var db = harness.NewDbContext();
        var files = await db.StoredFiles
            .Where(f => f.TenantId == seeded.TenantId && f.DeletedAt == null)
            .ToListAsync();

        files.Should().ContainSingle().Which.DriveFileId.Should().Be(seeded.DriveFileId);
    }

    /// <summary>
    /// Every signed input is signed. Change one and the URL is worth nothing.
    ///
    /// <para>Each case here is a real way a leaked URL gets repurposed: pointing it at a different
    /// key, extending its own lifetime, or bolting on a parameter the signer never agreed to. The
    /// last is the one that would slip through a canonical query built out of a fixed list of
    /// parameter names instead of everything that arrived.</para>
    /// </summary>
    [Fact]
    public async Task Changing_anything_the_url_signed_makes_it_worthless()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        harness.SeedLink("zq40mkx9");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);
        var path = $"/s3/{bucket}/{seeded.FileName}";

        using var client = harness.NewClient();
        var signed = Presign(client, HttpMethod.Get, path, id, secret);

        var tampered = new[]
        {
            // Aimed at another key in the same bucket.
            signed.Replace(Encode(seeded.FileName), Encode("payroll.xlsx"), StringComparison.Ordinal),

            // Its own lifetime stretched from an hour to a week.
            signed.Replace("X-Amz-Expires=3600", "X-Amz-Expires=604800", StringComparison.Ordinal),

            // A parameter added afterwards. S3 signs the whole query, so «afterwards» does not exist.
            signed + "&response-content-disposition=attachment",

            // The signature itself, one character along.
            signed[..^1] + (signed[^1] == 'a' ? 'b' : 'a'),
        };

        foreach (var url in tampered)
        {
            using var response = await client.GetAsync(url);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
            (await response.Content.ReadAsStringAsync()).Should().Contain("SignatureDoesNotMatch");
        }
    }

    /// <summary>
    /// A query that claims to be presigned and cannot be read as one is a parameter error, not a
    /// signature error.
    ///
    /// <para>S3 answers all of these with <c>400 AuthorizationQueryParametersError</c>, and the
    /// distinction is worth keeping: <c>SignatureDoesNotMatch</c> sends a developer looking at their
    /// secret, which is the wrong place when what they actually did was ask for eight days.</para>
    /// </summary>
    [Fact]
    public async Task A_query_that_cannot_be_read_as_a_signature_is_a_query_parameter_error()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);
        var path = $"/s3/{bucket}/{seeded.FileName}";

        using var client = harness.NewClient();

        var expiries = new[]
        {
            "604801",               // one second past AWS's own week-long cap
            "0",                    // a URL that was never valid for any instant
            "-1",                   // negative, which every signer refuses to produce
            "3600.0",               // seconds, not a quantity of them
            "an hour",              // not a number at all
            "99999999999999999999", // wider than a long, which must refuse rather than throw
        };

        foreach (var expires in expiries)
        {
            using var response = await client.GetAsync(
                Presign(client, HttpMethod.Get, path, id, secret, expires: expires));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, expires);
            (await response.Content.ReadAsStringAsync())
                .Should().Contain("AuthorizationQueryParametersError", expires);
        }

        // And a URL that carries a signature with nothing to check it against. Refused as a
        // parameter error rather than as «you did not sign this», which would be the wrong advice.
        var signed = Presign(client, HttpMethod.Get, path, id, secret);
        var beheaded = signed[..signed.IndexOf("&X-Amz-Signature=", StringComparison.Ordinal)];

        using var missing = await client.GetAsync(beheaded);

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("AuthorizationQueryParametersError");
    }

    /// <summary>
    /// A presigned URL is inside the same tenant fence as everything else.
    ///
    /// <para>Correctly signed, and aimed at somebody else's bucket. <c>NoSuchBucket</c> rather than
    /// <c>AccessDenied</c> for the same reason the header suite gives: telling them apart would
    /// confirm that a workspace by that slug exists, and a slug is a name somebody could guess.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_url_reaches_no_further_than_its_own_workspace()
    {
        await using var harness = new PublicSiteHarness();
        var mine = harness.SeedLink("kx91mzq4");
        var theirs = harness.SeedLink("zq40mkx9");
        var (id, secret) = await MintAsync(harness, mine.TenantId, ApiScope.Read);
        var theirBucket = SlugOf(harness, theirs.TenantId);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(Presign(
            client, HttpMethod.Get, $"/s3/{theirBucket}/{theirs.FileName}", id, secret));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NoSuchBucket");
    }

    /// <summary>
    /// An encrypted object is refused through a presigned URL exactly as it is through a signed
    /// header.
    ///
    /// <para>The gateway holds no key and cannot decrypt, and a presigned URL is the shape most
    /// likely to be handed to somebody who will simply click it. Serving the ciphertext would give
    /// them a file of the right name and length that opens as nothing — which is worse than a
    /// refusal, because it looks like it worked.</para>
    /// </summary>
    [Fact]
    public async Task A_presigned_url_cannot_prise_out_an_object_the_gateway_cannot_decrypt()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        Lock(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await client.GetAsync(Presign(
            client, HttpMethod.Get, $"/s3/{bucket}/{seeded.FileName}", id, secret));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("InvalidObjectState");
    }

    /// <summary>
    /// Revoking a key kills every URL it ever signed, including ones already in somebody's hands.
    ///
    /// <para>This is the whole mitigation a presigned URL has. It is a bearer credential with a
    /// lifetime of up to a week that nobody can recall once it is sent, so «revoke the key» has to
    /// be the answer — and it only is if the signature is checked against a live credential on every
    /// request rather than at the moment the link was made.</para>
    /// </summary>
    [Fact]
    public async Task Revoking_the_key_kills_the_urls_it_already_signed()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        using var client = harness.NewClient();
        var url = Presign(client, HttpMethod.Get, $"/s3/{bucket}/{seeded.FileName}", id, secret);

        using (var before = await client.GetAsync(url))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using (var db = harness.NewDbContext())
        {
            await db.S3Credentials
                .Where(c => c.AccessKeyId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, DateTimeOffset.UtcNow));
        }

        using var after = await client.GetAsync(url);

        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await after.Content.ReadAsStringAsync()).Should().Contain("InvalidAccessKeyId");
    }

    // ------------------------------------------------------------------ presigning, from the spec

    /// <summary>
    /// A presigned URL, built the way <c>aws s3 presign</c> and the SDKs build one.
    ///
    /// <para>Five parameters go in, sorted by encoded name, and the signature is appended to exactly
    /// the string that was signed — which is why the wire query and the canonical query cannot drift
    /// apart here any more than they can in a real client.</para>
    /// </summary>
    private static string Presign(
        HttpClient client,
        HttpMethod method,
        string path,
        string accessKeyId,
        string secret,
        DateTimeOffset? at = null,
        string expires = "3600",
        IEnumerable<(string Name, string Value)>? extra = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{dateStamp}/{Region}/{Service}/aws4_request";
        var host = client.BaseAddress!.Authority;

        var parameters = new List<(string Name, string Value)>
        {
            ("X-Amz-Algorithm", "AWS4-HMAC-SHA256"),
            ("X-Amz-Credential", $"{accessKeyId}/{scope}"),
            ("X-Amz-Date", amzDate),
            ("X-Amz-Expires", expires),

            // «host» and nothing else, which is what a presigned URL signs: there are no other
            // headers, because the caller of the URL is a browser nobody controls.
            ("X-Amz-SignedHeaders", "host"),
        };

        if (extra is not null) parameters.AddRange(extra);

        // Sorted by encoded name, not by the order they are written above.
        var canonicalQuery = string.Join(
            '&',
            parameters
                .Select(p => (Name: Encode(p.Name), Value: Encode(p.Value)))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value}"));

        var canonicalUri = string.Join('/', path.Split('/').Select(Encode));

        var canonicalRequest = string.Join(
            '\n',
            method.Method,
            canonicalUri,
            canonicalQuery,
            $"host:{host}\n",
            "host",

            // There is no body to hash and there never was one: the URL is made before the request
            // exists. The literal is what goes into the canonical request.
            "UNSIGNED-PAYLOAD");

        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var kDate = Hmac(Encoding.UTF8.GetBytes($"AWS4{secret}"), dateStamp);
        var kRegion = Hmac(kDate, Region);
        var kService = Hmac(kRegion, Service);
        var kSigning = Hmac(kService, "aws4_request");

        var signature = Convert.ToHexStringLower(Hmac(kSigning, stringToSign));

        return $"{canonicalUri}?{canonicalQuery}&X-Amz-Signature={signature}";
    }

    /// <summary>AWS's percent-encoding, written out rather than borrowed from the code under test.</summary>
    private static string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
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

    private static byte[] Hmac(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    // ------------------------------------------------------------------ seeding

    private static async Task<(string AccessKeyId, string Secret)> MintAsync(
        PublicSiteHarness harness,
        Guid tenantId,
        ApiScope scope)
    {
        await using var db = harness.NewDbContext();
        var store = new S3CredentialStore(db, harness.Protector, TimeProvider.System);

        var minted = await store.MintAsync(tenantId, Guid.NewGuid(), "test", scope, default);

        return (minted.Minted!.Credential.AccessKeyId, minted.Minted!.Secret);
    }

    private static string SlugOf(PublicSiteHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();

        return db.Tenants.AsNoTracking().First(t => t.Id == tenantId).Slug;
    }

    /// <summary>Makes the workspace's one file an encrypted one, the way the browser would.</summary>
    private static void Lock(PublicSiteHarness harness, Guid tenantId)
    {
        using var db = harness.NewDbContext();
        var file = db.StoredFiles.First(f => f.TenantId == tenantId && f.DeletedAt == null);

        db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = tenantId,
            Scheme = 1,
            SegmentSize = 1024 * 1024,
            NoncePrefix = "AAAAAAAAAAA=",
            PlaintextLength = file.SizeBytes,
            KdfSalt = "BBBBBBBBBBBBBBBBBBBBBB==",
            KdfIterations = 600_000,
            WrappedKey = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.SaveChanges();
    }
}
