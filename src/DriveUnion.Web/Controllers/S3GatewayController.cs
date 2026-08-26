using System.Globalization;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.S3;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// An S3-compatible front door onto a workspace.
///
/// <para><b>Under <c>/s3</c> and not at the root</b>, because the root is the panel. S3 clients take
/// a path in their endpoint — <c>--endpoint-url https://host/s3</c> for the AWS CLI,
/// <c>endpoint_url</c> for boto3 — so this costs a caller one segment of configuration and costs the
/// product nothing. Path-style addressing only: virtual-host style would need a wildcard certificate
/// and a subdomain per workspace, which is an infrastructure decision and not a code one.</para>
///
/// <para><b>One bucket per workspace</b>, named by its slug. S3's model is buckets of flat keys; this
/// product's is one workspace with a real folder tree. The reconciliation is in
/// <see cref="IS3Objects"/>, and a key is the folder path and the file name joined with a slash.</para>
///
/// <para><b>What is in this cut</b>, so nobody discovers the edges by hitting them: ListBuckets,
/// ListObjectsV2, HeadObject, GetObject with Range, PutObject and DeleteObject. Multipart upload is
/// <i>not</i> — which means the AWS CLI can copy files below its multipart threshold (8 MB by
/// default, raisable with <c>--expected-size</c> or a config change) and not above it. Everything
/// needed for it is in place; assembling out-of-order parts needs staging that this does not have
/// yet.</para>
/// </summary>
[ApiController]
[Route("s3")]
[EnableRateLimiting(DriveUnionRateLimits.Api)]
public sealed class S3GatewayController(
    S3RequestAuthenticator authenticator,
    IS3Objects objects,
    IStoredFileBytes bytes,
    IFileCatalog catalog,
    IUploadCoordinator uploads,
    IDriveClient drive,
    DriveUnionDbContext db) : ControllerBase
{
    /// <summary>What S3 defaults to and caps at, matching AWS so a client's paging behaves.</summary>
    private const int DefaultMaxKeys = 1000;

    [HttpGet("")]
    public async Task<IActionResult> ListBuckets(CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);

        var tenant = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == auth.Signer!.TenantId)
            .Select(t => new { t.Slug, t.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null) return Refuse(S3Refusal.AccessDenied);

        // One bucket, because a credential belongs to one workspace. A client that lists buckets is
        // asking «what can I reach», and the honest answer is «this».
        return Xml(S3Xml.ListAllMyBuckets(tenant.Slug, tenant.CreatedAt, auth.Signer!.TenantId.ToString()));
    }

    [HttpGet("{bucket}")]
    public async Task<IActionResult> ListObjects(
        string bucket,
        [FromQuery] string? prefix,
        [FromQuery] string? delimiter,
        [FromQuery(Name = "continuation-token")] string? continuationToken,
        [FromQuery(Name = "max-keys")] int? maxKeys,
        CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);
        if (!await OwnsAsync(auth.Signer!, bucket, cancellationToken)) return NoSuchBucket(bucket);

        var listing = await objects.ListAsync(
            auth.Signer!.TenantId,
            prefix,
            delimiter,
            continuationToken,
            Math.Clamp(maxKeys ?? DefaultMaxKeys, 1, DefaultMaxKeys),
            cancellationToken);

        return Xml(S3Xml.ListObjectsV2(bucket, listing, prefix, delimiter, maxKeys ?? DefaultMaxKeys));
    }

    [HttpHead("{bucket}/{*key}")]
    public async Task<IActionResult> HeadObject(string bucket, string key, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);
        if (!await OwnsAsync(auth.Signer!, bucket, cancellationToken)) return NoSuchBucket(bucket);

        var located = await objects.LocateAsync(auth.Signer!.TenantId, key, cancellationToken);

        // A HEAD carries no body, so an S3 error document would be discarded — the status is the
        // whole answer, and every client reads a 404 here as «no such key».
        if (located is null) return StatusCode(StatusCodes.Status404NotFound);

        WriteObjectHeaders(located);

        return new EmptyResult();
    }

    [HttpGet("{bucket}/{*key}")]
    public async Task<IActionResult> GetObject(string bucket, string key, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);
        if (!await OwnsAsync(auth.Signer!, bucket, cancellationToken)) return NoSuchBucket(bucket);

        var located = await objects.LocateAsync(auth.Signer!.TenantId, key, cancellationToken);
        if (located is null) return NoSuchKey(key);

        var stored = await bytes.ResolveAsync(auth.Signer!.TenantId, located.FileId, cancellationToken);
        if (stored is null) return NoSuchKey(key);

        var range = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;
        var download = await drive.OpenDownloadAsync(stored.GoogleAccountId, stored.DriveFileId, range, cancellationToken);

        await using (download)
        {
            Response.StatusCode = download.IsPartial
                ? StatusCodes.Status206PartialContent
                : StatusCodes.Status200OK;

            WriteObjectHeaders(located);
            Response.ContentType = stored.MimeType;

            if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
            if (download.ContentLength is { } length) Response.ContentLength = length;

            await download.Content.CopyToAsync(Response.Body, cancellationToken);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Stores an object.
    ///
    /// <para>The body is streamed to Drive and never held — through <see cref="AwsChunkedStream"/>
    /// first when the client signed a streaming payload, or the stored file is the object with
    /// several hundred bytes of chunk framing sprinkled through it and nothing fails.</para>
    ///
    /// <para>A PUT to an existing key replaces: the new object is stored and the old file goes to
    /// the trash. S3 keys are unique and this product's names are not, and replacing is the
    /// semantics a caller has already written their program against.</para>
    /// </summary>
    [HttpPut("{bucket}/{*key}")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> PutObject(string bucket, string key, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);
        if (!S3Permissions.MayWrite(auth.Signer!.Scope)) return Refuse(S3Refusal.AccessDenied);
        if (!await OwnsAsync(auth.Signer, bucket, cancellationToken)) return NoSuchBucket(bucket);

        var name = key.Trim('/').Split('/').LastOrDefault();

        if (string.IsNullOrEmpty(name)) return Error(StatusCodes.Status400BadRequest, "InvalidArgument", "The key names no object.");

        // The declared object length. With aws-chunked the Content-Length covers the framing too,
        // so the client's own header is what says how big the object is — and without either there
        // is nothing to open a resumable session with.
        var declared = Request.Headers["x-amz-decoded-content-length"].ToString();

        if (!long.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            if (Request.ContentLength is not { } contentLength)
            {
                return Error(StatusCodes.Status411LengthRequired, "MissingContentLength", "The object's length was not declared.");
            }

            size = contentLength;
        }

        var (folderId, refused) = await objects.EnsurePathAsync(auth.Signer.TenantId, auth.Signer.OwnerUserId, key, cancellationToken);

        if (refused != FolderOutcome.Done)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidArgument", "The key's path could not be created.");
        }

        var replacing = await objects.LocateAsync(auth.Signer.TenantId, key, cancellationToken);

        var begun = await uploads.BeginAsync(
            auth.Signer.TenantId,
            auth.Signer.OwnerUserId,
            new BeginUploadRequest(name, Request.ContentType ?? "application/octet-stream", size),
            cancellationToken);

        var body = auth.PayloadHash == SignatureV4.StreamingPayload
            ? new AwsChunkedStream(Request.Body)
            : Request.Body;

        var progress = await uploads.WriteChunkAsync(auth.Signer.TenantId, begun.SessionId, body, 0, size, cancellationToken);

        if (progress.StoredFileId is not { } storedId)
        {
            return Error(StatusCodes.Status500InternalServerError, "InternalError", progress.FailureReason ?? "The object was not stored.");
        }

        if (folderId is not null)
        {
            await db.StoredFiles
                .Where(f => f.Id == storedId && f.TenantId == auth.Signer.TenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.FolderId, folderId), cancellationToken);
        }

        // The old one goes after the new one has landed, never before: a replace that failed halfway
        // must leave the customer with the object they had rather than with neither.
        if (replacing is not null)
        {
            await catalog.DeleteAsync(auth.Signer.TenantId, replacing.FileId, cancellationToken);
        }

        Response.Headers.ETag = $"\"{storedId:N}\"";

        return Ok();
    }

    [HttpDelete("{bucket}/{*key}")]
    public async Task<IActionResult> DeleteObject(string bucket, string key, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(Request, cancellationToken);
        if (!auth.Succeeded) return Refuse(auth.Refusal);
        if (!S3Permissions.MayWrite(auth.Signer!.Scope)) return Refuse(S3Refusal.AccessDenied);
        if (!await OwnsAsync(auth.Signer, bucket, cancellationToken)) return NoSuchBucket(bucket);

        var located = await objects.LocateAsync(auth.Signer.TenantId, key, cancellationToken);

        // To the trash, like every other delete in this product. S3 answers 204 whether or not the
        // key was there, and that is its specification rather than an oversight of ours.
        if (located is not null) await catalog.DeleteAsync(auth.Signer.TenantId, located.FileId, cancellationToken);

        return NoContent();
    }

    private void WriteObjectHeaders(S3Located located)
    {
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.ETag = $"\"{located.FileId:N}\"";
        Response.Headers.LastModified = located.ModifiedAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
    }

    private async Task<bool> OwnsAsync(S3Signer signer, string bucket, CancellationToken cancellationToken) =>
        await db.Tenants.AnyAsync(t => t.Id == signer.TenantId && t.Slug == bucket, cancellationToken);

    private IActionResult Refuse(S3Refusal refusal) => refusal switch
    {
        S3Refusal.InvalidAccessKeyId => Error(StatusCodes.Status403Forbidden, "InvalidAccessKeyId", "That access key does not exist."),
        S3Refusal.SignatureDoesNotMatch => Error(StatusCodes.Status403Forbidden, "SignatureDoesNotMatch", "The request signature does not match."),
        S3Refusal.RequestTimeTooSkewed => Error(StatusCodes.Status403Forbidden, "RequestTimeTooSkewed", "The request time is too far from the server's."),
        S3Refusal.MissingSecurityHeader => Error(StatusCodes.Status400BadRequest, "MissingSecurityHeader", "The request was not signed with AWS4-HMAC-SHA256."),
        _ => Error(StatusCodes.Status403Forbidden, "AccessDenied", "That credential may not do this."),
    };

    private IActionResult NoSuchBucket(string bucket) =>
        Error(StatusCodes.Status404NotFound, "NoSuchBucket", $"There is no bucket called «{bucket}» here.");

    private IActionResult NoSuchKey(string key) =>
        Error(StatusCodes.Status404NotFound, "NoSuchKey", $"There is no object at «{key}».");

    private IActionResult Error(int status, string code, string message) => new ContentResult
    {
        StatusCode = status,
        ContentType = "application/xml",
        Content = S3Xml.Error(code, message, Request.Path, HttpContext.TraceIdentifier),
    };

    private IActionResult Xml(string body) => new ContentResult
    {
        StatusCode = StatusCodes.Status200OK,
        ContentType = "application/xml",
        Content = body,
    };
}
