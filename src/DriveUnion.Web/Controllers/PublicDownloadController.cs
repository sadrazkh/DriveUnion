using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// The public path — the part the product is actually sold on.
///
/// Anonymous, and deliberately without any tenant concept: <see cref="IPublicLinkReader"/> queries
/// by slug alone. A reader that took a tenant id would be handed <c>Guid.Empty</c> here and would
/// refuse every live link in the product while the rows sat plainly in the table.
/// </summary>
[AllowAnonymous]
public sealed class PublicDownloadController(
    IPublicLinkReader links,
    IDriveClient drive,
    IDownloadIpHasher ipHasher,
    IOptions<DriveUnionWebOptions> options,
    ILogger<PublicDownloadController> logger) : Controller
{
    /// <summary>DownloadEvent.UserAgent is varchar(512); a longer header would fail the insert.</summary>
    private const int UserAgentLimit = 512;

    [HttpGet("/d/{slug}")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicPage)]
    public async Task<IActionResult> Landing(
        string slug,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(lang);

        // A slug that cannot exist never reaches the database, and renders the same card as one that
        // simply does not.
        if (!SlugGenerator.IsWellFormed(slug)) return Unavailable(language);

        var resolution = await links.ResolveAsync(slug, cancellationToken);
        if (!resolution.IsAvailable || resolution.File is null)
        {
            if (resolution.Reason is { } reason)
            {
                logger.LogInformation("Public link refused: {Reason}", reason);
            }

            return Unavailable(language);
        }

        var file = resolution.File;
        var now = DateTimeOffset.UtcNow;
        var days = DisplayFormats.DaysUntil(file.ExpiresAt, now);
        var baseUrl = PublicBaseUrl();

        var model = new PublicDownloadViewModel(
            language,
            file.FileName,
            DisplayFormats.Bytes(file.SizeBytes),
            DisplayFormats.FileKind(file.FileName, file.MimeType),
            language == PublicLanguage.Fa
                ? DisplayFormats.PersianDate(file.CreatedAt)
                : DisplayFormats.IsoDate(file.CreatedAt),
            ExpiryText(days, language),
            language == PublicLanguage.Fa
                ? $"{PersianDigits.Count(file.DownloadCount)} بار دانلود شده"
                : $"Downloaded {file.DownloadCount} times",
            PublicLinkFormatter.Display(baseUrl, file.Slug),
            $"{PublicLinkFormatter.Path(file.Slug)}/file");

        // The count on the card moves and the link can be revoked while a copy sits in a cache.
        Response.Headers.CacheControl = "no-store";

        // Explicit path because the views live under Views/Public/ while this controller is named
        // for what it does rather than for its folder.
        return View("~/Views/Public/Download.cshtml", model);
    }

    /// <summary>
    /// The bytes.
    ///
    /// The client's <c>Range</c> goes to Drive untouched and Drive's answer — its status, its
    /// <c>Content-Range</c> — is what comes back, so seeking and resuming behave. The body is copied
    /// from one stream to the other and never materialised: a 214 GB file has to cost this server a
    /// buffer, not a copy. Nothing about Google appears in the response; there is no redirect to
    /// drive.google.com, and the file id and account email stay on this side of the wire.
    /// </summary>
    [HttpGet("/d/{slug}/file")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicDownload)]
    public async Task<IActionResult> Download(
        string slug,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(lang);

        if (!SlugGenerator.IsWellFormed(slug)) return Unavailable(language);

        var ticket = await links.ResolveForDownloadAsync(slug, cancellationToken);
        if (ticket is null) return Unavailable(language);

        var rangeHeader = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;

        DriveDownload download;
        try
        {
            download = await drive.OpenDownloadAsync(
                ticket.GoogleAccountId,
                ticket.DriveFileId,
                rangeHeader,
                cancellationToken);
        }
        catch (DriveApiException exception)
        {
            // Not the "no longer available" card: the link is fine and storage is not. Saying
            // otherwise would send the visitor away from a file that will be there in a minute.
            logger.LogError(exception, "Opening the Drive stream for a public link failed");
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        await using (download)
        {
            // Only a GET arrives here — HEAD is routed to Probe, which has no recorder in it — so
            // the Range header is the whole question, which is exactly what DownloadCounting takes.
            if (DownloadCounting.CountsAsDownload(rangeHeader))
            {
                await RecordAsync(ticket.ShareLinkId, cancellationToken);
            }

            Response.StatusCode = download.IsPartial
                ? StatusCodes.Status206PartialContent
                : StatusCodes.Status200OK;
            WriteFileHeaders(ticket);

            if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
            if (download.ContentLength is { } contentLength) Response.ContentLength = contentLength;

            try
            {
                await download.Content.CopyToAsync(Response.Body, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The visitor closed the tab or the player stopped. Not an error, and there is
                // nothing left to tell them.
            }
            catch (Exception exception)
            {
                // The status line left the building long ago; the only honest move is to break the
                // response so the client sees a truncated transfer and resumes with a Range.
                logger.LogError(exception, "The Drive stream failed mid-response; aborting the connection");
                HttpContext.Abort();
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// The probe.
    ///
    /// Video players ask HEAD for the length and for <c>Accept-Ranges</c> before they open a stream,
    /// and a 405 is enough to make some of them give up on a file that plays perfectly. It answers
    /// with the headers <see cref="Download"/> would send for an unranged GET, and no body.
    ///
    /// A probe is not a download, and this action is where that is enforced rather than remembered:
    /// there is no call to the recorder in it to guard or to forget. It does not reach Drive either.
    /// The size, name and type are all on the ticket, so a probe costs one row read instead of a
    /// connection to Google on the one anonymous, publicly-guessable route in the product.
    ///
    /// A <c>Range</c> on a HEAD is ignored — there is no content to partition, and honouring it
    /// would mean asking Drive for a 206 whose body is thrown away.
    /// </summary>
    [HttpHead("/d/{slug}/file")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicDownload)]
    public async Task<IActionResult> Probe(
        string slug,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(lang);

        if (!SlugGenerator.IsWellFormed(slug)) return Unavailable(language);

        var ticket = await links.ResolveForDownloadAsync(slug, cancellationToken);
        if (ticket is null) return Unavailable(language);

        Response.StatusCode = StatusCodes.Status200OK;
        WriteFileHeaders(ticket);

        // StoredFile.SizeBytes is what Drive reported when the upload completed, which is the same
        // number an unranged GET gets back in Content-Length.
        Response.ContentLength = ticket.SizeBytes;

        return new EmptyResult();
    }

    /// <summary>
    /// The headers the stream and the probe agree on, in one place — a player must not be told two
    /// different things about the same file depending on which verb it used.
    /// </summary>
    private void WriteFileHeaders(PublicDownloadTicket ticket)
    {
        Response.ContentType = string.IsNullOrWhiteSpace(ticket.MimeType)
            ? "application/octet-stream"
            : ticket.MimeType;
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.ContentDisposition = Disposition(ticket.FileName);
    }

    private async Task RecordAsync(Guid shareLinkId, CancellationToken cancellationToken)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > UserAgentLimit) userAgent = userAgent[..UserAgentLimit];

        await links.RecordDownloadAsync(
            shareLinkId,
            ipHasher.Hash(HttpContext.Connection.RemoteIpAddress),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            cancellationToken);
    }

    /// <summary>
    /// One card for revoked, expired, capped and never-existed — same status, same body, same
    /// headers. Any difference between them is an oracle that turns the slug space into something
    /// worth walking.
    /// </summary>
    private ViewResult Unavailable(PublicLanguage language)
    {
        Response.Headers.CacheControl = "no-store";

        var result = View("~/Views/Public/Unavailable.cshtml", new PublicUnavailableViewModel(language));
        result.StatusCode = StatusCodes.Status404NotFound;

        return result;
    }

    private static string Disposition(string fileName)
    {
        // The names are Persian. SetHttpFileName writes both the RFC 5987 filename* and an ASCII
        // fallback for clients that cannot read it, which is exactly the pair RFC 6266 asks for.
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);

        return disposition.ToString();
    }

    private PublicLanguage ResolveLanguage(string? requested) =>
        PublicLanguageResolver.Resolve(requested, Request.Headers.AcceptLanguage.ToString());

    private static string ExpiryText(int? days, PublicLanguage language) => days switch
    {
        null => PublicText.Pick(language, "بدون انقضا", "No expiry"),
        0 => PublicText.Pick(language, "امروز", "Today"),
        _ => language == PublicLanguage.Fa
            ? $"{PersianDigits.Plain(days.Value)} روز"
            : $"{days} days",
    };

    private string PublicBaseUrl() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";
}
