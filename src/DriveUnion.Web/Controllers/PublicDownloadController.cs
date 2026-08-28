using System.Text.Json;
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
    ITrafficMeter traffic,
    IEgressAllowance allowance,
    IOptions<DriveUnionWebOptions> options,
    ILogger<PublicDownloadController> logger) : Controller
{
    /// <summary>DownloadEvent.UserAgent is varchar(512); a longer header would fail the insert.</summary>
    private const int UserAgentLimit = 512;

    /// <summary>
    /// camelCase, because the only reader is the browser and the format's TypeScript definition is
    /// where the field names actually live. Nothing here is escaped into HTML by this serialiser —
    /// the view writes it into an attribute, which Razor encodes.
    /// </summary>
    private static readonly JsonSerializerOptions PublicJson =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A screen whose only job is playing this link's file.
    ///
    /// <para>The player used to be a box on the card, which is a narrow column already holding a
    /// title, four facts, a note and a download button — on a phone the controls landed on the text.
    /// Watching is what the link was sent for; it gets a screen.</para>
    ///
    /// <para>It resolves the slug again rather than trusting the page it was reached from. Every
    /// refusal a link can have is a refusal here too: revoked between the card and this press,
    /// expired, spent. And they collapse into the same card the landing page uses, for the same
    /// reason — the difference between them is an oracle for walking the slug space.</para>
    ///
    /// <para>Locked files come here as well, which the card's own unlock control does not do. Both
    /// are one player: the passphrase is typed on the page, the key is unwrapped in the browser, and
    /// the service worker decrypts a segment at a time. Nothing is fetched by opening this.</para>
    /// </summary>
    [HttpGet("/d/{slug}/watch")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicPage)]
    public async Task<IActionResult> Watch(
        string slug,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(lang);

        if (!SlugGenerator.IsWellFormed(slug)) return Unavailable(language);

        var resolution = await links.ResolveAsync(slug, cancellationToken);
        if (!resolution.IsAvailable || resolution.File is null) return Unavailable(language);

        var file = resolution.File;

        // What it would be if the lock came off, which for an unlocked file is simply what it is.
        var kind = Previews.OnceUnlocked(file.MimeType) switch
        {
            PreviewKind.Video => "video",
            PreviewKind.Audio => "audio",
            _ => string.Empty,
        };

        // Not something a browser can play. Back to the card, which has the download button and the
        // facts — a player page for a PDF would be an empty stage and a puzzled reader.
        if (kind.Length == 0) return Redirect(PublicLinkFormatter.Path(slug));

        ViewBag.Lang = PublicText.LangCode(language);

        // Never stored: the link can be revoked while a copy of this page sits in a cache, and the
        // page carries the header that says which passphrase opens the file.
        Response.Headers.CacheControl = "no-store";

        return View("~/Views/Public/Watch.cshtml", new WatchViewModel(
            file.FileName,
            DisplayFormats.Bytes(file.Encryption?.PlaintextLength ?? file.SizeBytes),
            DisplayFormats.FileKind(file.FileName, file.MimeType),
            kind,
            file.MimeType ?? string.Empty,

            // The ordinary download address. Pressing play spends a download exactly as pressing
            // Download would, because watching the film is taking the file; the seeks afterwards
            // carry a Range and spend nothing, which is the line DownloadCounting already draws.
            $"{PublicLinkFormatter.Path(slug)}/file",
            file.Encryption is { } header ? JsonSerializer.Serialize(header, PublicJson) : null,
            PublicLinkFormatter.Path(slug),
            PublicText.Pick(language, "بازگشت", "Back")));
    }

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
            // The file's own size for a locked one, not the ciphertext's: the visitor is about to
            // receive the file, and the tags that make the stored figure larger are not part of it.
            DisplayFormats.Bytes(file.Encryption?.PlaintextLength ?? file.SizeBytes),
            DisplayFormats.FileKind(file.FileName, file.MimeType),
            language == PublicLanguage.Fa
                ? DisplayFormats.PersianDate(file.CreatedAt)
                : DisplayFormats.IsoDate(file.CreatedAt),
            ExpiryText(days, language),
            language == PublicLanguage.Fa
                ? $"{PersianDigits.Count(file.DownloadCount)} بار دانلود شده"
                : $"Downloaded {file.DownloadCount} times",
            PublicLinkFormatter.Display(baseUrl, file.Slug),
            $"{PublicLinkFormatter.Path(file.Slug)}/file",
            file.Encryption is { } header
                ? JsonSerializer.Serialize(header, PublicJson)
                : null,
            file.SharedBy,
            file.Note,
            file.Preview,
            $"{PublicLinkFormatter.Path(file.Slug)}/preview",
            file.Slug,

            // Only for a locked file, and only for something a browser can play. An unlocked video
            // is already drawn by the preview above; asking this question about it too would put a
            // second player on the same card.
            file.Encryption is null
                ? PreviewKind.None
                : Previews.OnceUnlocked(file.MimeType) switch
                {
                    PreviewKind.Video => PreviewKind.Video,
                    PreviewKind.Audio => PreviewKind.Audio,

                    // Images and PDFs are deliberately not offered. Both are read whole, so
                    // decrypting one into the page — which the unlock card already does — is the
                    // whole of what a player would add, and it would add a second way to do it.
                    // Streaming earns its complexity on the files that are too big to wait for.
                    _ => PreviewKind.None,
                },
            file.MimeType ?? string.Empty,

            // A film too big to preview, which is most films. Only when there is no preview already
            // — under the ceiling one is drawn and a second control beside it would be two ways to
            // do one thing — and never for an encrypted file, which has its own player on the unlock
            // card and no bytes anybody can read without the key.
            file.Encryption is null && file.Preview == PreviewKind.None
                ? Previews.OnceUnlocked(file.MimeType) switch
                {
                    PreviewKind.Video => "video",
                    PreviewKind.Audio => "audio",
                    _ => string.Empty,
                }
                : string.Empty,

            $"{PublicLinkFormatter.Path(file.Slug)}/watch");

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
        return await StreamAsync(
            ticket,
            rangeHeader,
            DownloadCounting.CountsAsDownload(rangeHeader),
            inline: false,
            language,
            cancellationToken);
    }

    /// <summary>
    /// The same bytes, for a page to draw rather than for somebody to keep.
    ///
    /// <para>Separate from <see cref="Download"/> for two reasons that both had to be true.
    /// <c>Content-Disposition: inline</c> is what makes a PDF appear in a frame instead of landing in
    /// the downloads folder, and it is also what turns a served file into something a browser will
    /// execute — so it is issued from one place, for one list of types, and never for anything a
    /// visitor could have chosen. And a page load is not a download: a link capped at five would be
    /// spent by five people looking at it.</para>
    ///
    /// <para>Not spending the cap is the awkward half, and <c>Previews.MostBytesToShowWhole</c> is
    /// what holds it — see there. The egress is metered either way, and <b>counted against the
    /// workspace's monthly allowance either way</b>: the link's cap counts deliveries the customer
    /// chose to allow, the allowance counts bytes the operator buys, and a route that put bytes on
    /// the wire outside the second would be a hole in it. <see cref="StreamAsync"/> holds both.</para>
    /// </summary>
    [HttpGet("/d/{slug}/preview")]
    [EnableRateLimiting(DriveUnionRateLimits.PublicDownload)]
    public async Task<IActionResult> Preview(
        string slug,
        [FromQuery(Name = "lang")] string? lang,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(lang);

        if (!SlugGenerator.IsWellFormed(slug)) return Unavailable(language);

        var ticket = await links.ResolveForDownloadAsync(slug, cancellationToken);
        if (ticket is null) return Unavailable(language);

        // Asked again here rather than trusted from the page that linked here. The page and this
        // route read the same rule, and this is the side of it that matters: the page can only offer
        // what somebody drew, and a URL is something anybody can type.
        if (ticket.IsEncrypted
            || ticket.SizeBytes > Previews.MostBytesToShowWhole
            || !Previews.MayBeInline(ticket.MimeType))
        {
            return Unavailable(language);
        }

        var rangeHeader = Request.Headers.Range.Count > 0 ? Request.Headers.Range.ToString() : null;

        return await StreamAsync(ticket, rangeHeader, counts: false, inline: true, language, cancellationToken);
    }

    /// <summary>
    /// Browser → this server → Google, one buffer at a time, for whichever of the two routes above
    /// asked.
    /// </summary>
    /// <param name="counts">
    /// Whether this request takes one of the link's downloads. False for a seek, for a resume, and
    /// for every preview.
    /// </param>
    /// <param name="inline">Whether the browser is being asked to render it rather than save it.</param>
    private async Task<IActionResult> StreamAsync(
        PublicDownloadTicket ticket,
        string? rangeHeader,
        bool counts,
        bool inline,
        PublicLanguage language,
        CancellationToken cancellationToken)
    {
        // ── the traffic gate ────────────────────────────────────────────────────────────────────
        //
        // Before Google is contacted, for the same reason the download slot is reserved before
        // Google is contacted: a refusal that arrives after the stream is open has already cost the
        // operator a connection to Drive, and on a 214 GB file it has already cost them bytes. Until
        // this line existed the plan's MonthlyEgressBytes was copied onto the tenant row and compared
        // by nobody, so a workspace on a 300 GB tier could serve ten terabytes and the only thing
        // that would ever have noticed is Google's bill.
        //
        // Read once, here, and never again for this transfer. A stream that starts under the
        // allowance finishes even though it ends over it, and that is a decision rather than an
        // omission: cutting a 40 GB download at 99% because a counter crossed a line while it was
        // running is a worse outcome than serving a little over — the visitor gets a corrupt file,
        // the customer gets a support ticket, and the operator saves nothing, because the bytes were
        // already on the wire when the line was crossed. The overage stops the NEXT transfer, which
        // is exactly the rule TenantStorageMeter.SettleAsync already applies to an upload that came
        // in larger than it declared. It also means several large transfers in flight at once can
        // carry a workspace past its cap together; that is bounded by what is in flight, and it is
        // the same trade the storage meter makes.
        //
        // Nothing is reserved and nothing is given back. Unlike the link's download cap, this is not
        // a slot two requests can take from each other — it is a running total the meter writes at
        // the end of every transfer, so there is nothing here to hold and no finally to undo. What
        // that costs is the paragraph above: the check is advisory for the length of the transfer it
        // admits.
        //
        // A preview is gated too. It deliberately does not spend one of the link's downloads — a page
        // load is not a download, and five people looking at a file must not exhaust a link capped at
        // five — but that cap counts deliveries the CUSTOMER chose to allow, and this one counts
        // bytes the OPERATOR pays Google for. A preview puts bytes on the wire, it is already metered
        // on the way out, and the landing page publishes its URL: exempting it would leave the cap
        // bypassable by anyone willing to request /preview in a loop, against every image and PDF in
        // the product. So the two routes share this gate and disagree only about the slot.
        //
        // Two things deliberately outside it. HEAD reaches neither Drive nor this method and sends no
        // body, so a probe costs the operator nothing and is answered. And the landing page is not
        // gated either: it serves none of the file, and a visitor who is going to be refused is
        // better served by a card that names the file and a button that explains the refusal
        // precisely than by a 503 with no sentence on it.
        var standing = await allowance.ReadAsync(ticket.TenantId, cancellationToken);

        if (standing.IsOverAllowance)
        {
            // The workspace is named because this is a server log an operator acts on — it is the
            // one fact that turns «somebody was refused» into a phone call. Nothing about it reaches
            // the response; the card below carries no workspace, no slug and no figures.
            logger.LogInformation(
                "A public transfer was refused: tenant {TenantId} is over its monthly egress allowance.",
                ticket.TenantId);

            return OverAllowance(language);
        }

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

        // What actually reached the visitor, which is not what was promised: a tab closed at 90% of
        // a 200 MB file cost the operator 180 MB of Google's egress and a player seeking through a
        // video pays for the ranges it asked for. Counted as the body is copied, so an abort leaves
        // the true figure behind rather than nothing or the whole file.
        var sent = 0L;

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
                WriteFileHeaders(ticket, inline);

                if (download.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange;
                if (download.ContentLength is { } contentLength) Response.ContentLength = contentLength;

                try
                {
                    await EgressCopy.CopyAsync(
                        download.Content,
                        Response.Body,
                        bytes => sent = bytes,
                        cancellationToken);

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

            // Outside the reservation, and that is the point: a seek and a resume carry a Range, do
            // not count as a download and reserve no slot — and they are still the operator's
            // egress. A meter that only counted what the cap counted would under-report every video
            // on the product by however many times it was scrubbed.
            //
            // Not the request's token, for the same reason the two lines above are not: when the
            // visitor is the one who cancelled it is already cancelled, and the bytes they took
            // would go uncounted for the one reason that is not a failure at all.
            if (sent > 0) await traffic.RecordAsync(ticket.TenantId, sent, CancellationToken.None);
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
    private void WriteFileHeaders(PublicDownloadTicket ticket, bool inline = false)
    {
        Response.ContentType = string.IsNullOrWhiteSpace(ticket.MimeType)
            ? "application/octet-stream"
            : ticket.MimeType;
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.ContentDisposition = Disposition(ticket.FileName, inline);

        // Belt and braces on the one route that renders. The type came from a browser at upload time
        // and is echoed back here, so a file lying about what it is must not be given a second chance
        // by a browser sniffing the bytes and deciding for itself.
        Response.Headers.XContentTypeOptions = "nosniff";
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

    /// <summary>
    /// The fifth refusal, and the one that is deliberately <b>not</b> the card above.
    ///
    /// <para><b>Why the collapse does not apply here.</b> <see cref="Unavailable"/> exists so that
    /// revoked, expired, capped and never-existed cannot be told apart — the thing it hides is that a
    /// slug was ever real, because that is what turns the slug space into something worth walking. By
    /// the time this method is reached the link has already resolved: it is active, unexpired,
    /// unspent, and on any other day of the month this very request would have answered with the
    /// file. So a distinct answer here reveals nothing a successful download does not reveal — the
    /// oracle it is accused of opening is one that a live link opens by working. What the collapse
    /// protects is the <i>dead</i> slug space, and this is not one.</para>
    ///
    /// <para><b>And the collapse would be a lie.</b> «این لینک دیگر در دسترس نیست» is true of the
    /// other four: each is a permanent property of the link, or one its owner chose. This is neither.
    /// It is the owner's account being out of traffic, it clears at the turn of the month, and the
    /// link works again with nothing done to it. Telling a visitor the link is gone makes them delete
    /// the email and makes the customer re-issue a link that fails identically — which is the product
    /// failing loudest on the customer's busiest month.</para>
    ///
    /// <para><b>503 rather than the alternatives.</b> Not 404: the thing exists. Not 402, which
    /// asserts the fix is money — <c>PlanLimitBodies</c> already refuses that reasoning for the panel,
    /// and here the fix may equally be waiting for the first of the month. Not 429, because nothing
    /// about this is a rate. Not 509, which is a widely-recognised invention with no RFC behind it.
    /// 503 is what «this works and cannot serve you right now» means, and <c>Retry-After</c> is the
    /// header for saying when — which for a calendar allowance is a date this method actually knows.</para>
    ///
    /// <para>The card names no workspace, no figure and no slug: the visitor is a stranger, and how
    /// much traffic somebody bought is between them and the operator.</para>
    /// </summary>
    private ViewResult OverAllowance(PublicLanguage language)
    {
        // The same no-store every refusal carries. It matters more here than anywhere else on this
        // controller: this is the one refusal that is guaranteed to stop being true, and a copy of it
        // in a shared cache would outlive the state it describes.
        Response.Headers.CacheControl = "no-store";

        // Midnight UTC on the first of next month, which is exactly when MonthAsync starts counting
        // a new window — so the header promises the moment the refusal actually lifts rather than a
        // round number of hours somebody picked.
        Response.Headers.RetryAfter = EgressWindow.NextResetHeader();

        var result = View(
            "~/Views/Public/OverTraffic.cshtml",
            new PublicOverTrafficViewModel(language));

        result.StatusCode = StatusCodes.Status503ServiceUnavailable;

        return result;
    }

    /// <param name="inline">
    /// Only ever true on <see cref="Preview"/>, and only for a type on <c>Previews</c>' list. This is
    /// the one word in the whole response that decides whether a browser saves a file or runs it.
    /// </param>
    private static string Disposition(string fileName, bool inline)
    {
        // The names are Persian. SetHttpFileName writes both the RFC 5987 filename* and an ASCII
        // fallback for clients that cannot read it, which is exactly the pair RFC 6266 asks for.
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
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
