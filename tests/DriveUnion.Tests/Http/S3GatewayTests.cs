using System.Globalization;
using DriveUnion.Tests.Fakes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DriveUnion.Core.Api;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Web.S3;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Http;

/// <summary>
/// The S3 gateway, driven by requests signed the way a real client signs them.
///
/// <para><b>The signing is done here, by hand, from the specification</b> — not by calling the
/// gateway's own <c>SignatureV4</c>. A test that signed with the same code the server verifies with
/// would pass whatever both of them did, including agreeing on a canonical form that no AWS client
/// produces. What is under test is interoperability, so the test has to be the other party.</para>
/// </summary>
public class S3GatewayTests
{
    private const string Region = "us-east-1";
    private const string Service = "s3";

    [Fact]
    public async Task A_signed_request_lists_the_workspaces_bucket()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();
        using var response = await SendAsync(client, HttpMethod.Get, "/s3", id, secret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("ListAllMyBucketsResult");
        body.Should().Contain(S3Xml.Namespace, "every client checks the namespace before it parses");
        body.Should().Contain(SlugOf(harness, seeded.TenantId));
    }

    [Fact]
    public async Task An_unsigned_request_and_a_badly_signed_one_are_both_refused_in_xml()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();

        using var bare = await client.GetAsync("/s3");
        bare.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bare.Content.ReadAsStringAsync()).Should().Contain("MissingSecurityHeader");

        // The right key with the wrong secret. This is the case that proves the signature is being
        // recomputed rather than the access key id being trusted on its own.
        using var wrong = await SendAsync(client, HttpMethod.Get, "/s3", id, secret + "x");
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("SignatureDoesNotMatch");

        using var unknown = await SendAsync(client, HttpMethod.Get, "/s3", "DUIAZZZZZZZZZZZZZZZZ", secret);
        unknown.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("InvalidAccessKeyId");
    }

    [Fact]
    public async Task A_stale_signature_is_refused_before_the_secret_is_even_looked_up()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);

        using var client = harness.NewClient();

        // A signature stays valid for as long as its timestamp is accepted, so the skew window is
        // the length of time a captured request keeps working.
        using var stale = await SendAsync(
            client, HttpMethod.Get, "/s3", id, secret, at: DateTimeOffset.UtcNow.AddHours(-2));

        stale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("RequestTimeTooSkewed");
    }

    [Fact]
    public async Task Listing_a_bucket_shows_the_folder_tree_as_keys_and_prefixes()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        await FileIntoFolderAsync(harness, seeded.TenantId, "Reports");

        using var client = harness.NewClient();

        using var flat = await SendAsync(client, HttpMethod.Get, $"/s3/{bucket}", id, secret);
        var flatBody = await flat.Content.ReadAsStringAsync();

        // Without a delimiter every object is a key, folder path and all.
        flatBody.Should().Contain($"Reports/{seeded.FileName}");

        using var nested = await SendAsync(
            client, HttpMethod.Get, $"/s3/{bucket}", id, secret, query: "delimiter=%2F&list-type=2");

        var nestedBody = await nested.Content.ReadAsStringAsync();

        // With one, the folder collapses into a CommonPrefix — which is how `aws s3 ls` draws a
        // directory over a store that, as far as S3 is concerned, has none.
        nestedBody.Should().Contain("<CommonPrefixes><Prefix>Reports/</Prefix>");
        nestedBody.Should().NotContain($"Reports/{seeded.FileName}");
    }

    [Fact]
    public async Task A_read_key_may_not_delete()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("kx91mzq4");
        var (id, secret) = await MintAsync(harness, seeded.TenantId, ApiScope.Read);
        var bucket = SlugOf(harness, seeded.TenantId);

        using var client = harness.NewClient();
        using var response = await SendAsync(
            client, HttpMethod.Delete, $"/s3/{bucket}/{seeded.FileName}", id, secret);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AccessDenied");

        await using var db = harness.NewDbContext();
        (await db.StoredFiles.CountAsync(f => f.TenantId == seeded.TenantId && f.DeletedAt == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task One_workspaces_credential_cannot_name_anothers_bucket()
    {
        await using var harness = new PublicSiteHarness();
        var mine = harness.SeedLink("kx91mzq4");
        var theirs = harness.SeedLink("zq40mkx9");
        var (id, secret) = await MintAsync(harness, mine.TenantId, ApiScope.Write);
        var theirBucket = SlugOf(harness, theirs.TenantId);

        using var client = harness.NewClient();
        using var response = await SendAsync(client, HttpMethod.Get, $"/s3/{theirBucket}", id, secret);

        // NoSuchBucket and not AccessDenied: a gateway that told them apart would confirm that a
        // workspace by that slug exists, which is a name somebody could guess.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NoSuchBucket");
    }

    [Fact]
    public void The_aws_chunked_decoder_returns_the_object_and_not_its_framing()
    {
        // The failure this prevents is silent: framing left in the body stores a file that is a few
        // hundred bytes too long and corrupt in the middle, with a 200 on the response.
        var payload = Encoding.UTF8.GetBytes(new string('x', 5000));
        var framed = Chunked(payload, chunkSize: 1024);

        using var source = new MemoryStream(framed);
        using var decoded = new AwsChunkedStream(source);
        using var output = new MemoryStream();

        decoded.CopyTo(output);

        output.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void A_truncated_aws_chunked_body_throws_rather_than_storing_a_short_file()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 5000));
        var framed = Chunked(payload, chunkSize: 1024);

        using var source = new MemoryStream(framed[..(framed.Length / 2)]);
        using var decoded = new AwsChunkedStream(source);
        using var output = new MemoryStream();

        // Truncating silently would store a short file and call it a success, which is worse than
        // failing: the customer has an object that is there and wrong.
        var copy = () => decoded.CopyTo(output);

        copy.Should().Throw<EndOfStreamException>();
    }

    // ------------------------------------------------------------------ signing, from the spec

    /// <summary>
    /// Builds and sends a SigV4-signed request the way boto3 does: <c>UNSIGNED-PAYLOAD</c> for a
    /// body-less call, host and the two amz headers signed.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string accessKeyId,
        string secret,
        string? query = null,
        DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string payloadHash = "UNSIGNED-PAYLOAD";

        var uri = query is { Length: > 0 } ? $"{path}?{query}" : path;
        var request = new HttpRequestMessage(method, uri);

        var host = client.BaseAddress!.Authority;

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var canonicalUri = string.Join('/', path.Split('/').Select(s => Encode(s)));
        var canonicalQuery = CanonicalQuery(query);

        var canonicalHeaders =
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join(
            '\n', method.Method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        var scope = $"{dateStamp}/{Region}/{Service}/aws4_request";

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

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");

        return await client.SendAsync(request);
    }

    private static string CanonicalQuery(string? query)
    {
        if (query is not { Length: > 0 }) return string.Empty;

        var pairs = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var cut = p.IndexOf('=', StringComparison.Ordinal);
                return cut < 0
                    ? (Name: p, Value: string.Empty)
                    : (Name: p[..cut], Value: p[(cut + 1)..]);
            })
            .Select(p => (Name: Encode(Uri.UnescapeDataString(p.Name)), Value: Encode(Uri.UnescapeDataString(p.Value))))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(p => $"{p.Name}={p.Value}"));
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

    /// <summary>The aws-chunked framing, built the way a client builds it.</summary>
    private static byte[] Chunked(byte[] payload, int chunkSize)
    {
        using var output = new MemoryStream();

        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, payload.Length - offset);
            var header = $"{length:x};chunk-signature={new string('0', 64)}\r\n";

            output.Write(Encoding.ASCII.GetBytes(header));
            output.Write(payload, offset, length);
            output.Write("\r\n"u8);
        }

        output.Write(Encoding.ASCII.GetBytes($"0;chunk-signature={new string('0', 64)}\r\n\r\n"));

        return output.ToArray();
    }

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

    private static async Task FileIntoFolderAsync(PublicSiteHarness harness, Guid tenantId, string folderName)
    {
        await using var db = harness.NewDbContext();
        var tree = new FolderTree(db, TimeProvider.System);

        var made = await tree.CreateAsync(tenantId, Guid.NewGuid(), null, folderName, default);
        var file = await db.StoredFiles.FirstAsync(f => f.TenantId == tenantId && f.DeletedAt == null);

        await tree.MoveFileAsync(tenantId, file.Id, made.FolderId, default);
    }
}
