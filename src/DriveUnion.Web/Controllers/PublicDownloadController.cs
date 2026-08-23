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
    ///
    /// The cap is <em>reserved</em> before Google is contacted, and given back if the download never
    /// happens. Checking it and spending it used to be two separate acts with the whole transfer
    /// between them, which on a 214 GB file is hours: a link at 499 of 500 with several downloads in
    /// flight served every one of them. Reserving first closes that window without charging for a
    /// stream Google dropped at byte 512 of 4096, because the slot goes back.
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

        // Only a GET arrives here — HEAD is routed to Probe, which has no recorder in it — so the
        // Range header is the whole question, which is exactly what DownloadCounting takes. A request
        // that does not count reserves nothing: a player's one-byte probe and a mid-file seek are
        // somebody continuing a download that has already been paid for.
        var counts = DownloadCounting.CountsAsDownload(rangeHeader);
        var reserved = false;

        if (counts)
        {
            // Not the request's token, for one statement that takes a millisecond: a reservation
            // cancelled between the write landing and this line being reached is a slot taken by
            // nobody and given back by nobody, on a link whose cap is the thing being protected.
            // Finishing it costs one UPDATE the finally below will undo.
            reserved = await links.TryReserveDownloadAsync(ticket.ShareLinkId, CancellationToken.None);

            // No slot left — the link was spent by requests that are still streaming, or by one that
            // finished between the resolve above and this line. The same card as revoked, expired and
            // never-existed: not a 429 and not a 409, either of which would tell a scanner it had
            // found a live link.
            if (!reserved) return Unavailable(language);
        }

        var delivered = false;

        try
        {
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
                Response.StatusCode = download.IsPartial
                    ? StatusCodes.Status206PartialContent
                    : StatusCodes.Status200OK;
                WriteFileHeaders(ticket);

                if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
                if (download.ContentLength is { } contentLength) Response.ContentLength = contentLength;

                try
                {
                    await download.Content.CopyToAsync(Response.Body, cancellationToken);
                    delivered = true;
                }
                catch (OperationCanceledException)
                {
                    // The visitor closed the tab or the player stopped. Not an error, and there is
                    // nothing left to tell them — but it is still their download and it still counts.
                    // This server did everything it was asked; a transfer abandoned at 99% must not be
                    // 214 GB of the operator's egress for free, again and again, against a cap it
                    // never touches.
                    delivered = true;
                }
                catch (Exception exception)
                {
                    // The status line left the building long ago; the only honest move is to break the
                    // response so the client sees a truncated transfer and resumes with a Range. The
                    // slot goes back in the finally: a stream Google dropped at byte 512 of 4096 is
                    // the operator's failure, and a customer's capped link must not pay for it.
                    logger.LogError(exception, "The Drive stream failed mid-response; aborting the connection");
                    HttpContext.Abort();
                }
            }

            return new EmptyResult();
        }
        finally
        {
            // Every reservation ends here, on every way out of this method — including the paths
            // nothing above catches, such as a request cancelled while Drive was still being opened.
            // A slot that is neither written down nor given back is a download the customer paid for
            // and nobody took.
            //
            // Not the request's token: when the visitor is the one who cancelled it is already
            // cancelled, and the download would go unrecorded for the one reason that is not a
            // failure at all — and a released slot would stay spent for the same reason.
            if (reserved)
            {
                if (delivered) await RecordAsync(ticket.ShareLinkId, CancellationToken.None);
                else await links.ReleaseDownloadAsync(ticket.ShareLinkId, CancellationToken.None);
            }
        }
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
