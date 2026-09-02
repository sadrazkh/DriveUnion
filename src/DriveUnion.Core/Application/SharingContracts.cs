using DriveUnion.Core.Sharing;

namespace DriveUnion.Core.Application;

/// <summary>What an edit did. Three answers, because two of them are refusals with reasons.</summary>
public enum ShareLinkEdit
{
    Changed,

    /// <summary>Not this workspace's link, or revoked. One answer for the reason it always is.</summary>
    NotFound,

    /// <summary>
    /// The new ceiling is below what the link has already been used for.
    ///
    /// <para>Refused rather than clamped or accepted. Accepting it would kill a live link on the
    /// spot in a way the person editing it did not ask for; clamping would silently store a number
    /// they did not type. The screen says both figures instead.</para>
    /// </summary>
    BelowWhatIsSpent,
}

public sealed record ShareLinkSummary(
    Guid Id,
    string Slug,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    int DownloadCount,
    bool IsActive,
    string? Note = null,

    /// <summary>
    /// Whether this link carries its own copy of the file's key.
    ///
    /// <para>The panel needs to tell the two apart: a link with one is opened by a secret the owner
    /// handed out for it, and a link without one is opened by the owner's own passphrase — which
    /// also opens everything else uploaded in that batch. Those are different things to have
    /// given somebody, and the screen says which.</para>
    /// </summary>
    bool HasOwnKey = false);

/// <param name="Note">
/// A line for whoever opens the link. Trimmed and cut to <c>ShareLink.MaxNoteLength</c> by the
/// service rather than refused: this is a sentence somebody typed into a box beside a button, and
/// losing the link because the sentence ran long would be the wrong trade.
/// </param>
/// <param name="Key">
/// The file's content key re-wrapped for this link, for a locked file the owner has just opened in
/// their browser — and null for everything else.
///
/// <para>Refused rather than trimmed when it is malformed, unlike <c>Note</c>: a note that lost its
/// last three characters is still a note, and a wrapped key that lost anything is a link nobody can
/// ever open. The two are different kinds of field and are treated differently on purpose.</para>
/// </param>
public sealed record CreateShareLinkRequest(
    Guid StoredFileId,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    string? Note = null,
    LinkKeyMaterial? Key = null);

/// <summary>The owner's side of a link. Tenant-scoped, like everything else in the panel.</summary>
public interface IShareLinkService
{
    Task<ShareLinkSummary> CreateAsync(
        Guid tenantId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShareLinkSummary>> ListForFileAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every link the tenant owns, newest first, each with the file it points at.
    ///
    /// The panel's «لینک‌های اشتراک» table is file · address · downloads · expiry · status, and a
    /// <see cref="ShareLinkSummary"/> on its own can name none of the first column and offer nowhere
    /// to go — so the row travels as a triple rather than as a fourth summary record that this one
    /// table would be the only reader of.
    ///
    /// <c>tenantId</c> is an argument and not an ambient filter, for the reason in §8 of the M1
    /// design: this product has no global query filter, because one would hand <c>Guid.Empty</c> to
    /// every anonymous /d/{slug} and refuse every live link in the product.
    /// </summary>
    Task<IReadOnlyList<(ShareLinkSummary Link, Guid StoredFileId, string FileName)>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes when a link stops working and how much it may be used for, and answers what happened.
    ///
    /// <para><b>Revocation is not reachable from here.</b> Revoking burns a slug for ever (M4 §2), so
    /// an edit that could set <c>IsActive</c> back to true would be an undo for the one action in
    /// this product that has none — and a slug handed out, revoked, and quietly working again is the
    /// worst version of a share link there is.</para>
    ///
    /// <para>An edit that <i>would</i> revive a link by raising its ceiling past what it has already
    /// spent is a different thing and is allowed: the link was never revoked, it ran out. That is the
    /// case this exists for.</para>
    /// </summary>
    Task<ShareLinkEdit> UpdateAsync(
        Guid tenantId,
        Guid linkId,
        DateTimeOffset? expiresAt,
        int? maxDownloads,
        string? note,
        CancellationToken cancellationToken);
}

/// <summary>What the public landing page shows. No account, no file id, no tenant.</summary>
/// <param name="SizeBytes">
/// What is stored, which for an encrypted file is the ciphertext and is longer than the file by one
/// tag per segment. <c>Encryption.PlaintextLength</c> is the number to show when there is one.
/// </param>
/// <param name="Encryption">
/// How to open it, for a file the uploader locked before sending — and null for every other file.
///
/// <para>Public, on an anonymous page, and that is not an oversight. None of it is secret: the salt
/// and the nonce prefix are public by construction, and the wrapped key is a key only to somebody
/// who also has the passphrase it is wrapped with. Whoever holds the link is who the owner meant to
/// give the file to; making them ask a second endpoint for the header would be one more round trip
/// protecting nothing.</para>
/// </param>
/// <param name="SharedBy">
/// The workspace's name, and the only fact about the owner that crosses to this page.
///
/// <para>Not a leak and not an accident: the visitor was given this link deliberately, and a page
/// that will not say who sent them a file is a page nobody trusts enough to press the button on.
/// It is the workspace's own name — the one they chose and put on their invoices — and not a user's
/// name or address, which is a different question the visitor was not asked to be told the answer
/// to.</para>
/// </param>
/// <param name="Note">What the sender wrote for this link's recipients, or null.</param>
/// <param name="Preview">
/// What the page may draw, decided in <see cref="Previews"/> rather than by the view.
///
/// <para>Here rather than in Razor because the same decision governs the route that serves the
/// bytes inline, and a page that offers a preview the route refuses — or worse, a route that serves
/// inline what the page would never have asked for — is the two halves of one rule drifting apart.
/// </para>
/// </param>
public sealed record PublicFileView(
    string Slug,
    string FileName,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    int DownloadCount,
    int? MaxDownloads,
    DateTimeOffset? ExpiresAt,
    EncryptionHeader? Encryption = null,
    string SharedBy = "",
    string? Note = null,
    PreviewKind Preview = PreviewKind.None);

/// <summary>
/// Everything the streaming route needs. <see cref="GoogleAccountId"/> and
/// <see cref="DriveFileId"/> stay server-side — they must never reach a response body, header or URL.
/// </summary>
/// <param name="TenantId">
/// Whose egress this transfer is, and the only reason a tenant appears on an anonymous path.
///
/// <para>The interface's own summary says this reader has no tenant and must not acquire one, and
/// that is still true of the <i>lookup</i>: nothing about resolving a slug is scoped by workspace,
/// because the visitor has none. This is the answer, not the question — the file that was found
/// belongs to somebody, and the bytes about to be sent are billed to them. It stays server-side
/// like the two below.</para>
/// </param>
/// <param name="IsEncrypted">
/// Whether the bytes are ciphertext. The streaming route serves them either way — that is the whole
/// design — but the <i>inline</i> route must not, because there is nothing to render and an inline
/// disposition is not something to hand out for a file nobody can read.
/// </param>
public sealed record PublicDownloadTicket(
    Guid ShareLinkId,
    Guid TenantId,
    Guid GoogleAccountId,
    string DriveFileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    bool IsEncrypted = false);

/// <summary>
/// A slug lookup. <c>Reason</c> is null when no such slug exists, and set when a real link is
/// refusing — a distinction for the logs and the owner's panel only. The visitor gets one identical
/// card either way, because telling "expired" apart from "never existed" is enough to enumerate the
/// slug space.
/// </summary>
public sealed record PublicLinkResolution(
    bool IsAvailable,
    ShareLinkAvailability? Reason,
    PublicFileView? File)
{
    public static readonly PublicLinkResolution NotFound = new(false, null, null);
}

/// <summary>
/// The public path, and the reason this interface exists separately from <see cref="IFileCatalog"/>.
///
/// /d/{slug} is anonymous. It has no tenant and must not acquire one: a reader that took a tenantId
/// would be handed <c>Guid.Empty</c> by an anonymous request and would 404 every live link in the
/// product while the rows sat plainly in the table. So this type has no tenant concept at all, and
/// that absence is load-bearing.
/// </summary>
public interface IPublicLinkReader
{
    Task<PublicLinkResolution> ResolveAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Null when the slug is unknown or the link is refusing. The caller renders one card.</summary>
    Task<PublicDownloadTicket?> ResolveForDownloadAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Takes one of the link's remaining downloads and says whether there was one left to take.
    ///
    /// This is the gate. The count <see cref="ResolveForDownloadAsync"/> reads is advisory — true
    /// when it was read and possibly false a moment later — and a transfer runs for as long as the
    /// file is big, so a link at 499 of 500 with several downloads in flight would hand out a 501st.
    /// The slot is therefore spent here, before Google is contacted, and the answer is the database's
    /// rather than this process's.
    ///
    /// False means no slot was left. The caller must answer with the same card it gives a revoked,
    /// expired or never-existed slug: a refusal that looked different — a 429, a 409, anything — is
    /// a fourth oracle for telling live slugs from dead ones.
    ///
    /// Every true must be finished by exactly one <see cref="RecordDownloadAsync"/> when the visitor
    /// took the bytes, or one <see cref="ReleaseDownloadAsync"/> when they did not.
    /// </summary>
    Task<bool> TryReserveDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes down a reservation the visitor consumed — the audit row behind the number, and
    /// nothing else. The counter already moved when the slot was reserved, so moving it again here
    /// would bill one download twice.
    ///
    /// The caller decides whether a request counts at all — see <see cref="DownloadCounting"/> —
    /// because that decision is about the Range header, not the row. A request that does not count
    /// reserves nothing and records nothing.
    /// </summary>
    Task RecordDownloadAsync(
        Guid shareLinkId,
        string ipHash,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gives a reservation back, for a download that never happened: Drive refused before the first
    /// byte, or the stream died mid-response. No audit row, because nothing was served.
    ///
    /// It cannot drive the count below zero. A caller that releases twice, or releases a slot it
    /// never reserved, must not be able to mint downloads out of a negative counter.
    /// </summary>
    Task ReleaseDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken);
}
